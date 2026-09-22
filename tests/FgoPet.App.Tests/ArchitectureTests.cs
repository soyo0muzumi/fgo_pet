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
    public void Dependency_direction_keeps_feature_modules_on_foundational_contracts()
    {
        var core = ReferencedProjects("FgoPet.Core");
        var infra = ReferencedProjects("FgoPet.Infrastructure");
        var app = ReferencedProjects("FgoPet.App");

        // Retiring the cross-module settings aggregate leaves Core with no
        // project references; speech settings stay owned by Speech.Core.
        Assert.Empty(core);
        Assert.Equal(
            new[] { "FgoPet.AgentProtocol", "FgoPet.AgentRuntime", "FgoPet.Core" }.OrderBy(name => name, StringComparer.Ordinal),
            infra.OrderBy(name => name, StringComparer.Ordinal));
        // 阶段 1 项目拆分（2026-09-21）：FgoPet.App 不再是唯一的应用程序集，
        // 各模块的 UI/应用层各自成工程（沿用 speech 的「独立程序集 + RootNamespace=FgoPet.App」范式），
        // 组合根只负责把 HostContracts/DesktopShell 与各模块串起来。
        // 因此这里的期望集从 3 个变为 15 个——方向不变（组合根 → 模块），只是模块不再是回链文件。
        Assert.Equal(
            new[]
            {
                "FgoPet.Character",
                "FgoPet.Core",
                "FgoPet.DataManagement",
                "FgoPet.DesktopShell",
                "FgoPet.Dialogue",
                "FgoPet.Focus",
                "FgoPet.HostContracts",
                "FgoPet.Infrastructure",
                "FgoPet.Memory",
                "FgoPet.SettingsHost",
                "FgoPet.Speech.Desktop",
                "FgoPet.UiFoundation",
                "FgoPet.Work.Archives",
                "FgoPet.Work.Execution",
                "FgoPet.Work.Todo",
            }.OrderBy(name => name, StringComparer.Ordinal),
            app.OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public void Only_tests_reference_the_composition_root()
    {
        // 拆分后 FgoPet.App 是唯一的组合根。任何库工程反向引用它都会把
        // 「模块 → 组合根」变成编译期环，模块也就无法脱离应用单独编译。
        var offenders = ProjectFiles()
            .Where(IsOnThisCheckout)
            .Where(path => !Path.GetFileName(path).EndsWith(".Tests.csproj", StringComparison.OrdinalIgnoreCase))
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
            Assert.True(panel.CanStartFocus);
        });
    }

    [Fact]
    public void Attached_panel_shell_brush_references_are_defined()
    {
        var root = RepoRoot();
        // ④ 迁移后这两个文件物理搬到了根级模块树，不再位于 src/FgoPet.App 下。
        var panel = File.ReadAllText(Path.Combine(root, "host", "DesktopShell", "Desktop", "AttachedPanelView.xaml"));
        var tokens = XDocument.Load(Path.Combine(root, "ui-foundation", "Shell", "ShellTokens.xaml"));
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
