using System.Text.RegularExpressions;
using Xunit;

namespace FgoPet.Architecture.Tests;

public sealed class ProjectDependencyGraphTests
{
    [Fact]
    public async Task Release_solution_has_no_evaluated_project_reference_cycles()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "FgoPet.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var paths = Regex.Matches(File.ReadAllText(Path.Combine(root!.FullName, "FgoPet.sln")), "\"([^\"]+\\.csproj)\"")
            .Select(match => Path.GetFullPath(Path.Combine(root.FullName, match.Groups[1].Value))).ToArray();
        Assert.NotEmpty(paths);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var count = await ProjectDependencyGraph.VerifyAcyclicAsync(paths,
            new DotNetMsBuildProjectReferenceEvaluator(), "Release", "win-x64", timeout.Token);
        Assert.True(count >= paths.Length);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    public async Task Evaluated_cycle_is_rejected_for_compile_and_companion_edges(string compile)
    {
        using var fixture = new GraphFixture(compile);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ProjectDependencyGraph.VerifyAcyclicAsync(
            [fixture.RootProject], new DotNetMsBuildProjectReferenceEvaluator(), "Release"));
        Assert.Contains("cycle", error.Message);
        Assert.Contains("A.csproj", error.Message);
        Assert.Contains("B.csproj", error.Message);
    }

    [Fact]
    public async Task Condition_false_edge_does_not_create_a_cycle()
    {
        using var fixture = new GraphFixture("true");
        var count = await ProjectDependencyGraph.VerifyAcyclicAsync([fixture.RootProject],
            new DotNetMsBuildProjectReferenceEvaluator(), "Debug");
        Assert.Equal(2, count);
    }

    private sealed class GraphFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "fgo-graph-" + Guid.NewGuid().ToString("N"));
        public string RootProject => Path.Combine(_root, "A.csproj");
        public GraphFixture(string compile)
        {
            Directory.CreateDirectory(_root);
            File.WriteAllText(RootProject, """
                <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
                <ItemGroup><ProjectReference Include="B.csproj" /></ItemGroup></Project>
                """);
            File.WriteAllText(Path.Combine(_root, "B.csproj"), $$"""
                <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
                <ItemGroup Condition="'$(Configuration)' == 'Release'"><ProjectReference Include="A.csproj" ReferenceOutputAssembly="{{compile}}" /></ItemGroup></Project>
                """);
        }
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
