using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace FgoPet.App.Tests.Framework;

public sealed class ArchitectureTests
{
    [Fact]
    public void Production_projects_do_not_reference_SkiaSharp()
    {
        var files = ProjectFiles().Where(IsOnThisCheckout);
        Assert.DoesNotContain(files, path => File.ReadAllText(path).Contains("SkiaSharp", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Production_projects_do_not_reference_the_rendering_spike()
    {
        var files = ProjectFiles().Where(IsOnThisCheckout);
        Assert.DoesNotContain(files, path =>
            File.ReadAllText(path).Contains("FgoPet.RenderingProbe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Core_has_no_renderer_or_transparency_selectors()
    {
        var core = ReadProject("FgoPet.Core");
        Assert.DoesNotContain(core, "RenderBackend", StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(core, "TransparencyMode", StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(core, "SkiaSharp", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Core_and_Infrastructure_do_not_use_WPF()
    {
        var expected =
            from name in new[] { "FgoPet.Core", "FgoPet.Infrastructure" }
            let text = ReadProject(name)
            select new { name, hasUseWpf = text.Contains("<UseWPF>true</UseWPF>", StringComparison.OrdinalIgnoreCase) };

        Assert.All(expected, item => Assert.False(item.hasUseWpf, $"{item.name} must not enable WPF."));
    }

    [Fact]
    public void Dependency_direction_is_App_to_Infrastructure_to_Core()
    {
        var core = ReferencedProjects("FgoPet.Core");
        var infra = ReferencedProjects("FgoPet.Infrastructure");
        var app = ReferencedProjects("FgoPet.App");

        Assert.Empty(core);
        Assert.Equal(new[] { "FgoPet.Core" }, infra.OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal(
            new[] { "FgoPet.Core", "FgoPet.Infrastructure" }.OrderBy(name => name, StringComparer.Ordinal),
            app.OrderBy(name => name, StringComparer.Ordinal));
    }

    private static IEnumerable<string> ProjectFiles() => FindCsproj(RepoRoot());

    private static bool IsOnThisCheckout(string path) =>
        !path.Contains($"{Path.DirectorySeparatorChar}spikes{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        && !path.Contains(".worktrees", StringComparison.Ordinal);

    private static string ReadProject(string projectName) =>
        File.ReadAllText(FindCsproj(RepoRoot()).Where(IsOnThisCheckout)
            .Single(path => Path.GetFileName(path).Equals($"{projectName}.csproj", StringComparison.OrdinalIgnoreCase)));

    private static IEnumerable<string> ReferencedProjects(string projectName)
    {
        var text = ReadProject(projectName);
        return Regex.Matches(
                text,
                @"<ProjectReference\s+Include\s*=\s*""[^""]*[/\\]([^/\\]+)\.csproj""",
                RegexOptions.IgnoreCase)
            .Select(match => match.Groups[1].Value)
            .ToArray();
    }

    private static IEnumerable<string> FindCsproj(string root)
    {
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".git", ".worktrees", "bin", "obj", ".pytest_cache", "__pycache__", ".venv", "venv",
        };
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }

            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(directory);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var child in children)
            {
                if (!excluded.Contains(Path.GetFileName(child)))
                {
                    pending.Push(child);
                }
            }
        }
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "FgoPet.sln")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new InvalidOperationException("FgoPet.sln was not found above the test output directory.");
    }
}