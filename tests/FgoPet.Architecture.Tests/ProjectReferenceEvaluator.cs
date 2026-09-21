using System.Diagnostics;
using System.Text.Json;

namespace FgoPet.Architecture.Tests;

internal enum ProjectReferenceKind
{
    Compile,
    Companion,
}

internal sealed record EvaluatedProjectReference(string ProjectPath, ProjectReferenceKind Kind);

internal sealed record EvaluatedProject(
    string ProjectPath,
    string TargetFramework,
    IReadOnlyList<string> RuntimeIdentifiers,
    IReadOnlyList<EvaluatedProjectReference> References);

internal interface IProjectReferenceEvaluator
{
    Task<EvaluatedProject> EvaluateAsync(
        string projectPath,
        string configuration,
        string? runtimeIdentifier,
        CancellationToken cancellationToken = default);
}

internal sealed class DotNetMsBuildProjectReferenceEvaluator : IProjectReferenceEvaluator
{
    private readonly IProcessFactory _processFactory;

    public DotNetMsBuildProjectReferenceEvaluator(IProcessFactory? processFactory = null) =>
        _processFactory = processFactory ?? new ProcessFactory();

    public async Task<EvaluatedProject> EvaluateAsync(
        string projectPath,
        string configuration,
        string? runtimeIdentifier = null,
        CancellationToken cancellationToken = default)
    {
        var fullProjectPath = Path.GetFullPath(projectPath);
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveDotNetHost(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("msbuild");
        startInfo.ArgumentList.Add(fullProjectPath);
        startInfo.ArgumentList.Add("-nologo");
        startInfo.ArgumentList.Add("-getProperty:TargetFramework,RuntimeIdentifiers");
        startInfo.ArgumentList.Add("-getItem:ProjectReference");
        startInfo.ArgumentList.Add($"-p:Configuration={configuration}");
        if (!string.IsNullOrWhiteSpace(runtimeIdentifier))
        {
            startInfo.ArgumentList.Add($"-p:RuntimeIdentifier={runtimeIdentifier}");
        }

        using var process = _processFactory.Start(startInfo);
        var standardOutput = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var standardError = process.StandardError.ReadToEndAsync(CancellationToken.None);
        int exitCode;
        try
        {
            exitCode = await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(standardOutput, standardError).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Preserve the caller's cancellation while still observing cleanup below.
            }

            try
            {
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Cleanup failures must not replace the original cancellation.
            }

            try
            {
                await Task.WhenAll(standardOutput, standardError).ConfigureAwait(false);
            }
            catch
            {
                // Observe both output tasks without replacing the original cancellation.
            }

            throw;
        }

        if (exitCode != 0)
        {
            throw new InvalidOperationException($"MSBuild evaluation failed for '{fullProjectPath}' with exit code {exitCode}.");
        }

        return Parse(fullProjectPath, standardOutput.Result);
    }

    private static EvaluatedProject Parse(string projectPath, string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("Properties", out var properties) || properties.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("Items", out var items) || items.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"MSBuild evaluation for '{projectPath}' returned an invalid result.");
        }

        if (!properties.TryGetProperty("TargetFramework", out var targetFrameworkElement) ||
            targetFrameworkElement.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(targetFrameworkElement.GetString()))
        {
            throw new InvalidOperationException($"MSBuild evaluation for '{projectPath}' omitted TargetFramework.");
        }

        var runtimeIdentifiers = Array.Empty<string>();
        if (properties.TryGetProperty("RuntimeIdentifiers", out var runtimeElement) &&
            runtimeElement.ValueKind == JsonValueKind.String)
        {
            runtimeIdentifiers = runtimeElement.GetString()!
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
                .ToArray();
        }

        if (!items.TryGetProperty("ProjectReference", out var referencesElement) ||
            referencesElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException($"MSBuild evaluation for '{projectPath}' omitted ProjectReference items.");
        }

        var projectDirectory = Path.GetDirectoryName(projectPath)!;
        var references = new List<EvaluatedProjectReference>();
        foreach (var item in referencesElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("Identity", out var identityElement))
            {
                throw new InvalidOperationException($"MSBuild evaluation for '{projectPath}' returned an invalid ProjectReference item.");
            }

            var identity = identityElement.GetString();
            if (string.IsNullOrWhiteSpace(identity))
            {
                throw new InvalidOperationException($"MSBuild evaluation for '{projectPath}' returned an empty ProjectReference item.");
            }

            var referencePath = identity;
            if (item.TryGetProperty("FullPath", out var fullPathElement) && fullPathElement.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(fullPathElement.GetString()))
            {
                referencePath = fullPathElement.GetString()!;
            }

            var kind = item.TryGetProperty("ReferenceOutputAssembly", out var outputElement) &&
                outputElement.ValueKind == JsonValueKind.String &&
                string.Equals(outputElement.GetString(), "false", StringComparison.OrdinalIgnoreCase)
                ? ProjectReferenceKind.Companion
                : ProjectReferenceKind.Compile;
            references.Add(new EvaluatedProjectReference(Path.GetFullPath(Path.IsPathRooted(referencePath)
                ? referencePath
                : Path.Combine(projectDirectory, referencePath)), kind));
        }

        return new EvaluatedProject(
            projectPath,
            targetFrameworkElement.GetString()!,
            runtimeIdentifiers,
            references);
    }

    private static string ResolveDotNetHost()
    {
        var configuredHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        return !string.IsNullOrWhiteSpace(configuredHost) && File.Exists(configuredHost)
            ? configuredHost
            : "dotnet";
    }
}

internal interface IProcessFactory
{
    IProcessHandle Start(ProcessStartInfo startInfo);
}

internal interface IProcessHandle : IDisposable
{
    StreamReader StandardOutput { get; }
    StreamReader StandardError { get; }
    bool HasExited { get; }
    Task<int> WaitForExitAsync(CancellationToken cancellationToken);
    void Kill(bool entireProcessTree);
}

internal sealed class ProcessFactory : IProcessFactory
{
    public IProcessHandle Start(ProcessStartInfo startInfo) => new ProcessHandle(Process.Start(startInfo) ??
        throw new InvalidOperationException("Unable to start dotnet msbuild."));
}

internal sealed class ProcessHandle : IProcessHandle
{
    private readonly Process _process;

    public ProcessHandle(Process process) => _process = process;

    public StreamReader StandardOutput => _process.StandardOutput;
    public StreamReader StandardError => _process.StandardError;
    public bool HasExited => _process.HasExited;
    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
    {
        await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return _process.ExitCode;
    }
    public void Kill(bool entireProcessTree) => _process.Kill(entireProcessTree);
    public void Dispose() => _process.Dispose();
}
