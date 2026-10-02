using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Windows;
using FgoPet.App.Bootstrap;
using FgoPet.App.Panels;
using FgoPet.App.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FgoPet.App.Tests.Framework;

public sealed class ArchitectureTests
{
    [Fact]
    public void Conversation_loop_prompting_and_budgeting_compile_in_the_pure_kernel()
    {
        var kernel = typeof(AppRuntime).Assembly;
        Assert.Same(kernel, typeof(FgoPet.App.Dialogue.ConversationOrchestrator).Assembly);
        Assert.Same(kernel, typeof(FgoPet.App.Dialogue.PromptComposer).Assembly);
        Assert.Same(kernel, typeof(FgoPet.App.Dialogue.ConversationSummaryService).Assembly);
        Assert.Same(kernel, typeof(FgoPet.Infrastructure.Providers.RequestTokenMeter).Assembly);
    }
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
    public void Kernel_has_no_renderer_or_transparency_selectors()
    {
        var core = ReadProject("FgoPet.Kernel");
        Assert.DoesNotContain(core, "RenderBackend", StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(core, "TransparencyMode", StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(core, "SkiaSharp", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Kernel_and_platform_implementations_do_not_use_WPF()
    {
        var expected =
            from name in new[] { "FgoPet.Kernel", "FgoPet.Platform.Contracts", "FgoPet.Platform.Storage", "FgoPet.Platform.Windows" }
            let text = ReadProject(name)
            select new { name, hasUseWpf = text.Contains("<UseWPF>true</UseWPF>", StringComparison.OrdinalIgnoreCase) };

        Assert.All(expected, item => Assert.False(item.hasUseWpf, $"{item.name} must not enable WPF."));
    }

    [Fact]
    public void Only_tests_reference_the_composition_root()
    {
        // 拆分后 FgoPet.App 是唯一的组合根。任何库工程反向引用它都会把
        // 「模块 → 组合根」变成编译期环，模块也就无法脱离应用单独编译。
        var offenders = ProjectFiles()
            .Where(IsOnThisCheckout)
            .Where(path => !Path.GetFileName(path).EndsWith(".Tests.csproj", StringComparison.OrdinalIgnoreCase))
            .Where(path => !IsTestProject(path))
            .Where(path => !Path.GetFileName(path).Equals("FgoPet.App.csproj", StringComparison.OrdinalIgnoreCase))
            .Where(path => XDocument.Parse(File.ReadAllText(path)).Descendants("ProjectReference")
                .Any(reference => string.Equals(
                    Path.GetFileNameWithoutExtension((string)reference.Attribute("Include")!),
                    "FgoPet.App",
                    StringComparison.OrdinalIgnoreCase)))
            .Select(path => Path.GetFileName(path)!)
            .ToArray();

        Assert.Empty(offenders);
    }

    private static bool IsTestProject(string path) => XDocument.Load(path).Descendants("IsTestProject")
        .Any(element => string.Equals(element.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void Explicit_test_project_metadata_is_recognized_even_without_a_tests_filename()
    {
        var path = Path.Combine(Path.GetTempPath(), "fgopet-project-" + Guid.NewGuid().ToString("N") + ".csproj");
        try
        {
            File.WriteAllText(path, "<Project><PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup></Project>");
            Assert.True(IsTestProject(path));
            File.WriteAllText(path, "<Project><PropertyGroup><IsTestProject>false</IsTestProject></PropertyGroup></Project>");
            Assert.False(IsTestProject(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Production_container_resolves_the_application_shell()
    {
        StaTest.Run(() =>
        {
            _ = Application.Current ?? new Application();
            using var provider = new ServiceCollection().AddFgoPet([]).BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });

            Assert.IsType<DesktopAppShell>(provider.GetRequiredService<IAppShell>());
        });
    }

    [Fact]
    public void Production_container_wires_runtime_role_state_to_the_focus_panel()
    {
        StaTest.Run(() =>
        {
            _ = Application.Current ?? new Application();
            using var provider = new ServiceCollection().AddFgoPet([]).BuildServiceProvider();
            var runtime = provider.GetRequiredService<AppRuntime>();
            var panel = provider.GetRequiredService<AttachedPanelViewModel>();

            runtime.SetActiveRole(new ActiveRoleState("pack", "casual", "1.0.0", "servant-mash"));

            Assert.Equal("servant-mash", panel.ActiveServantId);
            Assert.True(provider.GetRequiredService<FgoPet.Plugin.Focus.Desktop.FocusCompactViewModel>().CanStartFocus);
        });
    }

    [Fact]
    public void Attached_panel_shell_brush_references_are_defined()
    {
        var root = RepoRoot();
        // ④ 迁移后这两个文件物理搬到了根级模块树，不再位于 src/FgoPet.App 下。
        var panel = File.ReadAllText(Path.Combine(root, "src", "FgoPet.Desktop", "Shell", "Desktop", "AttachedPanelView.xaml"));
        var tokens = XDocument.Load(Path.Combine(root, "src", "FgoPet.UiSdk", "Resources", "Shell", "ShellTokens.xaml"));
        var definedKeys = tokens.Descendants()
            .Select(element => element.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value)
            .Where(key => key is not null)
            .ToHashSet(StringComparer.Ordinal);
        var referencedKeys = Regex.Matches(panel, @"\{DynamicResource\s+(Shell\w+Brush)\}")
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.All(referencedKeys, key => Assert.Contains(key, definedKeys));
    }

    private static IEnumerable<string> ProjectFiles() => FindCsproj(RepoRoot());

    private static bool IsOnThisCheckout(string path) =>
        !path.Contains($"{Path.DirectorySeparatorChar}spikes{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    [Fact]
    public void Checkout_filter_accepts_project_paths_from_a_worktree_checkout()
    {
        var projectPath = Path.Combine(
            Path.GetTempPath(),
            ".worktrees",
            "phase4-runtime-repair",
            "src",
            "FgoPet.Core",
            "FgoPet.Core.csproj");

        Assert.True(IsOnThisCheckout(projectPath));
    }

    private static string ReadProject(string projectName) =>
        File.ReadAllText(FindCsproj(RepoRoot()).Where(IsOnThisCheckout)
            .Single(path => Path.GetFileName(path).Equals($"{projectName}.csproj", StringComparison.OrdinalIgnoreCase)));

    private static IEnumerable<string> ReferencedProjects(string projectName)
    {
        // Companion executables are build/publish dependencies, not assembly references.
        return XDocument.Parse(ReadProject(projectName)).Descendants("ProjectReference")
            .Where(reference => !string.Equals((string?)reference.Attribute("ReferenceOutputAssembly"), "false", StringComparison.OrdinalIgnoreCase))
            .Select(reference => Path.GetFileNameWithoutExtension((string)reference.Attribute("Include")!))
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
