using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace FgoPet.Architecture.Tests;

public sealed class MigrationRepositoryTests
{
    [Fact]
    public async Task Evaluated_repository_obeys_target_policy_or_exact_shrinking_debt()
    {
        var root = FindRoot();
        var policy = new StrictJsonArchitecturePolicyReader().Read(File.ReadAllText(Path.Combine(root, "tests/architecture/policy.json")), "policy.json");
        var baseline = MigrationRepository.ReadBaseline(File.ReadAllText(Path.Combine(root, "tests/architecture/baseline.json")));
        var originPath = Path.Combine(root, "tests/architecture/baseline-origin.json");
        Assert.Equal("56560204D117EB0D7ABEF9CC9A3231EF3F1B30ACD8A08D27A0F4D7B14A146831",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(File.ReadAllText(originPath).Replace("\r\n", "\n").TrimEnd('\n')))));
        var original = MigrationRepository.ReadBaseline(File.ReadAllText(originPath));
        var snapshots = new System.Collections.Concurrent.ConcurrentBag<MigrationProject>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var evaluations = MigrationRepository.ProjectPaths(root).ToArray().SelectMany(project =>
            new[] { "Debug", "Release" }.SelectMany(configuration =>
                new string?[] { null, "win-x64" }.Select(rid => (project, configuration, rid))));
        await Parallel.ForEachAsync(evaluations,
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = deadline.Token },
            async (evaluation, token) => snapshots.Add(await MigrationRepository.EvaluateAsync(
                root, evaluation.project, evaluation.configuration, evaluation.rid, token)));
        Assert.Empty(MigrationBoundaryGate.Verify(policy, baseline, original, snapshots));
        // Compare both uncommitted edits and committed changes. A removed original exception cannot return later.
        foreach (var revision in new[] { "HEAD", "HEAD^" })
        {
            var historical = await MigrationRepository.ReadHistoricalBaselineAsync(root, revision, deadline.Token);
            if (historical is not null) Assert.Empty(MigrationBoundaryGate.Verify(policy, baseline, historical, snapshots));
        }
    }

    [Fact]
    public async Task Imported_conditional_compilation_and_companion_reference_are_evaluated()
    {
        using var fixture = new EvaluationFixture();
        var release = await MigrationRepository.EvaluateAsync(fixture.Root, "A.csproj", "Release", null, CancellationToken.None);
        var debug = await MigrationRepository.EvaluateAsync(fixture.Root, "A.csproj", "Debug", null, CancellationToken.None);
        Assert.Equal("extra.cs", Assert.Single(release.LinkedSources));
        Assert.Equal("companion", Assert.Single(release.References).Kind);
        Assert.Empty(debug.LinkedSources);
        Assert.Empty(debug.References);
    }

    [Theory]
    [InlineData("{\"schemaVersion\":1,\"schemaVersion\":1,\"legacyCompileItems\":[],\"dependencyExceptions\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"legacyCompileItems\":[],\"dependencyExceptions\":[],\"extra\":true}")]
    [InlineData("{\"schemaVersion\":1,\"legacyCompileItems\":[{\"project\":\"src/A.csproj\",\"source\":\"../secret.cs\"}],\"dependencyExceptions\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"legacyCompileItems\":[],\"dependencyExceptions\":[{\"from\":\"src/A.csproj\",\"to\":\"src/B.csproj\",\"kind\":\"ignored\"}]}")]
    [InlineData("{\"schemaVersion\":1}")]
    public void Invalid_baseline_fails_closed(string json) => Assert.ThrowsAny<Exception>(() => MigrationRepository.ReadBaseline(json));

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "FgoPet.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("Repository root not found.");
    }

    private sealed class EvaluationFixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "fgo-migration-" + Guid.NewGuid().ToString("N"));
        internal EvaluationFixture()
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(Path.Combine(Root, "A.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><Import Project="extra.props" /></Project>
                """);
            File.WriteAllText(Path.Combine(Root, "extra.props"), """
                <Project><ItemGroup Condition="'$(Configuration)' == 'Release'"><Compile Include="extra.cs" /><ProjectReference Include="B.csproj" ReferenceOutputAssembly="false" /></ItemGroup></Project>
                """);
            File.WriteAllText(Path.Combine(Root, "B.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(Root, "extra.cs"), "");
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}

internal static class MigrationRepository
{
    internal static async Task<MigrationBaseline?> ReadHistoricalBaselineAsync(string root, string revision, CancellationToken token)
    {
        return await ReadHistoricalBaselineAtPathAsync(root, revision, "tests/architecture/baseline.json", token)
            ?? await ReadHistoricalBaselineAtPathAsync(root, revision, "architecture/baseline.json", token);
    }

    private static async Task<MigrationBaseline?> ReadHistoricalBaselineAtPathAsync(string root, string revision, string path, CancellationToken token)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-c", "safe.directory=" + root.Replace('\\', '/'), "show", revision + ":" + path }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Git history could not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
            throw;
        }
        await Task.WhenAll(stdout, stderr);
        if (process.ExitCode == 0) return ReadBaseline(await stdout);
        var error = await stderr;
        if (error.Contains("does not exist in", StringComparison.Ordinal) || error.Contains("exists on disk, but not in", StringComparison.Ordinal)) return null;
        throw new InvalidOperationException("Cannot verify migration baseline history.");
    }

    internal static IEnumerable<string> ProjectPaths(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            foreach (var project in Directory.EnumerateFiles(directory, "*.csproj")) yield return Relative(root, project);
            foreach (var child in Directory.EnumerateDirectories(directory))
                if (Path.GetFileName(child) is not (".git" or ".worktrees" or "bin" or "obj" or "artifacts" or "spikes" or ".superpowers")) pending.Push(child);
        }
    }

    internal static async Task<MigrationProject> EvaluateAsync(string root, string project, string configuration,
        string? rid, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "msbuild", Path.Combine(root, project), "-nologo", "-getProperty:IsTestProject", "-getItem:Compile,ProjectReference", "-p:Configuration=" + configuration }) start.ArgumentList.Add(argument);
        if (rid is not null) start.ArgumentList.Add("-p:RuntimeIdentifier=" + rid);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("MSBuild process could not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(cancellationToken); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
            throw;
        }
        await Task.WhenAll(stdout, stderr);
        if (process.ExitCode != 0) throw new InvalidOperationException("MSBuild evaluation failed: " + project);
        using var document = JsonDocument.Parse(await stdout);
        var items = document.RootElement.GetProperty("Items");
        var sources = items.GetProperty("Compile").EnumerateArray().Select(item => Relative(root, item.GetProperty("FullPath").GetString()!)).ToImmutableArray();
        var edges = items.GetProperty("ProjectReference").EnumerateArray().Select(item => new DependencyDebt(project,
            Relative(root, item.GetProperty("FullPath").GetString()!),
            item.TryGetProperty("ReferenceOutputAssembly", out var value) && string.Equals(value.GetString(), "false", StringComparison.OrdinalIgnoreCase) ? "companion" : "compile")).ToImmutableArray();
        var isTest = string.Equals(document.RootElement.GetProperty("Properties").GetProperty("IsTestProject").GetString(), "true", StringComparison.OrdinalIgnoreCase);
        return new(project, sources, edges, isTest);
    }

    internal static MigrationBaseline ReadBaseline(string json)
    {
        using var document = JsonDocument.Parse(json);
        RejectDuplicates(document.RootElement);
        var result = JsonSerializer.Deserialize<MigrationBaseline>(json, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        }) ?? throw new InvalidDataException("Missing migration baseline.");
        if (result.SchemaVersion != 1 || result.LegacyCompileItems.IsDefault || result.DependencyExceptions.IsDefault)
            throw new InvalidDataException("Invalid migration baseline schema.");
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in result.LegacyCompileItems)
        {
            ValidatePath(item.Project); ValidatePath(item.Source);
            if (!identities.Add(item.Project + " -> " + item.Source)) throw new InvalidDataException("Duplicate compile exception.");
        }
        identities.Clear();
        foreach (var edge in result.DependencyExceptions)
        {
            ValidatePath(edge.From); ValidatePath(edge.To);
            if (edge.Kind is not ("compile" or "companion")) throw new InvalidDataException("Invalid dependency kind.");
            if (!identities.Add(edge.From + " -> " + edge.To + " " + edge.Kind)) throw new InvalidDataException("Duplicate dependency exception.");
        }
        return result;
    }

    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate baseline property.");
                RejectDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var child in element.EnumerateArray()) RejectDuplicates(child);
    }

    private static string Relative(string root, string path)
    {
        var relative = Path.GetRelativePath(root, Path.GetFullPath(path)).Replace('\\', '/');
        ValidatePath(relative);
        return relative;
    }
    private static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains('\\') || path.Contains(':')
            || path.Contains('*') || path.Contains('?') || path.Any(char.IsControl) || path.Split('/').Any(segment => segment is "" or "." or ".."))
            throw new InvalidDataException("Migration path must stay within the repository.");
    }
}
