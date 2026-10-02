using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using FgoPet.App.Servants;
using FgoPet.App.Settings;
using FgoPet.Character.Settings;
using FgoPet.Core.Geometry;
using FgoPet.Core.Packs;
using FgoPet.Core.Portraits;
using FgoPet.Core.Settings;
using FgoPet.Extensibility;
using FgoPet.UiSdk;
using Microsoft.Web.WebView2.Wpf;
using Xunit;
using Xunit.Abstractions;

namespace FgoPet.Windows.Tests.Settings;

public sealed class RolePackagesWebTests
{
    private readonly ITestOutputHelper _output;
    private int _requestSequence;

    public RolePackagesWebTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Real_settings_root_renders_role_catalog_and_detail_without_duplicate_title_or_narrow_overflow()
    {
        await StaRunner.RunAsync(async () =>
        {
            // Existing owner normalizes new package defaults on first detail load.
            // Start with normalized settings so the navigation assertion measures extra writes.
            var settings = new CharacterStore(CharacterSettings.Defaults with
            {
                PackageSettings = new Dictionary<string, IReadOnlyDictionary<string, string>>
                {
                    ["synthetic_servant"] = new Dictionary<string, string> { ["greeting"] = "你好" },
                },
            });
            var repository = new FakeRepository();
            var installer = new FakeInstaller();
            var portrait = new FakePortraitController();
            var library = new ServantLibraryViewModel(repository, installer, portrait, settings, _ => { });
            var owner = new RolePackagesWebPage(settings, library);
            var catalog = new SettingsPageCatalog(
            [
                new SettingsPageViewFactory("RolePackages", _ => throw new InvalidOperationException(
                    "RolePackages is rendered by the Web settings root."), "角色包", group: "内容", order: 10),
            ]);
            var factory = new SettingsWebRootFactory(catalog, [owner], Path.Combine(Path.GetTempPath(),
                "fgopet-role-packages-root-" + Guid.NewGuid().ToString("N")));
            var navigation = new SettingsViewModel(SettingsSection.RolePackages);
            var window = new SettingsWindow(navigation, factory)
            {
                Width = 560,
                Height = 740,
                ShowInTaskbar = false,
                Left = -10000,
                Top = -10000,
            };
            var host = Assert.IsType<WebView2SurfaceHost>(window.Content);

            try
            {
                window.Show();
                LogBoundary("webview-start", "role-packages-root", "settings.navigate", "started");
                await WaitUntil(() => host.State == WebSurfaceState.Ready);
                var web = Assert.Single(host.Children.OfType<WebView2>());
                await WaitUntil(async () => await Script(web,
                    "document.querySelector('[data-package-card]')?.dataset.packageId ?? ''") == "community.sample");
                LogBoundary("webview-ready", "role-packages-root", "rolePackages.get", "success");

                Assert.Equal("1", await Script(web, "document.querySelectorAll('main h1#page-title').length"));
                Assert.Equal("角色包", await Script(web, "document.querySelector('#page-title').textContent"));
                Assert.Equal("0", await Script(web, "document.querySelector('[data-role-packages-page] h1') ? '1' : '0'"));
                Assert.Equal("community.sample", await Script(web,
                    "document.querySelector('[data-package-card]')?.dataset.packageId ?? ''"));
                Assert.Equal(0, installer.InstallCount);
                Assert.Empty(portrait.Activations);

                navigation.OpenPackageCommand.Execute(new PackageDetailRoute("community.sample", "测试角色"));
                await WaitUntil(async () => await Script(web,
                    "document.querySelector('[data-detail-header] [data-detail-name]')?.textContent ?? ''") == "测试角色");
                navigation.BackToPackagesCommand.Execute(null);
                await WaitUntil(async () => await Script(web,
                    "document.querySelector('[data-route=catalog]').hidden ? 'hidden' : 'catalog'") == "catalog");
                await web.CoreWebView2.ExecuteScriptAsync(
                    "document.querySelector('[data-open-package]').click() ");
                await WaitUntil(async () => await Script(web,
                    "document.querySelector('[data-detail-header] [data-detail-name]')?.textContent ?? ''") == "测试角色");
                LogBoundary("webview-navigation", "role-packages-root", "rolePackages.open", "success");
                Assert.Equal("detail", await Script(web, "document.querySelector('[data-route=detail]').hidden ? 'hidden' : 'detail'"));

                await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('[data-section=packageInfo]').click()");
                await WaitUntil(async () => await Script(web,
                    "document.querySelector('[data-section-panel=packageInfo]').hidden ? 'hidden' : 'visible'") == "visible");
                Assert.Contains("community.sample", await Script(web,
                    "document.querySelector('[data-section-panel=packageInfo]').innerText"));

                window.Width = 320;
                await WaitUntil(async () =>
                {
                    var widthText = await Script(web, "String(document.documentElement.clientWidth)");
                    return int.TryParse(widthText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var width)
                        && width is >= 260 and <= 320;
                });
                Assert.Equal("true", await Script(web,
                    "document.documentElement.scrollWidth <= document.documentElement.clientWidth"));
                Assert.Equal("true", await Script(web,
                    "document.querySelector('main').scrollWidth <= document.querySelector('main').clientWidth"));
                LogBoundary("webview-narrow", "role-packages-root", "layout.check", "success");
                Assert.Equal(0, settings.SaveCount);
            }
            finally
            {
                window.Dispose();
            }

            await WaitUntil(() => host.State == WebSurfaceState.Closed);
        });
    }

    [Fact]
    public async Task Built_outputs_include_role_package_module_and_stylesheet_at_root_pages_path()
    {
        var destination = Path.Combine(AppContext.BaseDirectory, "Desktop", "ui", "settings", "root", "pages");
        var script = await File.ReadAllTextAsync(Path.Combine(destination, "role-packages.js"));
        var style = await File.ReadAllTextAsync(Path.Combine(destination, "role-packages.css"));
        Assert.Contains("rolePackages.get", script);
        Assert.Contains("data-role-packages-page", script);
        Assert.Contains("rolePackages.activate", script);
        Assert.Contains("rp-detail", style);
    }

    private void LogBoundary(string phase, string requestId, string command, string outcome,
        string? errorCode = null) => _output.WriteLine(JsonSerializer.Serialize(new
        {
            time = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            phase,
            requestId = $"{requestId}-{Interlocked.Increment(ref _requestSequence).ToString(CultureInfo.InvariantCulture)}",
            command,
            outcome,
            errorCode,
        }));

    private static async Task<string> Script(WebView2 web, string code)
    {
        using var value = JsonDocument.Parse(await web.CoreWebView2.ExecuteScriptAsync(code));
        return value.RootElement.ValueKind == JsonValueKind.String
            ? value.RootElement.GetString() ?? string.Empty : value.RootElement.GetRawText();
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(12);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(25);
        Assert.True(condition(), "The RolePackages WebView did not reach the expected state.");
    }

    private static async Task WaitUntil(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(12);
        while (!await condition() && DateTime.UtcNow < deadline) await Task.Delay(25);
        Assert.True(await condition(), "The RolePackages WebView did not reach the expected state.");
    }

    private sealed class CharacterStore(CharacterSettings initial) : ICharacterSettingsStore
    {
        public CharacterSettings Value { get; private set; } = initial;
        public int SaveCount { get; private set; }
        public CharacterSettings Load() => Value;
        public void Save(CharacterSettings settings)
        {
            SaveCount++;
            Value = settings;
        }
    }

    private sealed class FakeRepository : IArtPackageRepository
    {
        private static readonly string Root = Path.Combine(Path.GetTempPath(), "synthetic-role-pack");
        private static readonly string Preview = Path.Combine(Root, "preview.png");
        private readonly InstalledServant _servant = new("community.sample", "synthetic_servant", "测试角色",
            Preview, "community", [new ServantAppearance("casual", "1.2.0", Root, Preview)])
        {
            PackageVersion = "1.2.0",
            MinAppVersion = "1.0.0",
            Settings = [new PackSettingDefinition { Key = "greeting", Label = "问候", Type = PackSettingType.Text, Default = "你好" }],
            DefaultAddress = "御主",
            Capabilities = ["art.v3"],
        };

        public Task<IReadOnlyList<InstalledServant>> ListServantsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<InstalledServant>>([_servant]);

        public Task<PackCatalog> ScanAsync(CancellationToken cancellationToken) => Task.FromResult(new PackCatalog(
        [new InstalledPack("community.sample", "1.2.0", SemVersion.Parse("1.2.0"), Root,
            "synthetic_servant", "测试角色", Preview, "community",
            [new AppearanceSlot("casual", "appearances/casual/manifest.json")]) ]));

        public Task<AppearanceLocation?> GetAppearanceAsync(PortraitSelection selection, CancellationToken cancellationToken) =>
            Task.FromResult<AppearanceLocation?>(null);

        public Task<AppearanceLocation?> ResolveStartupSelectionAsync(PortraitSelection? requested, CancellationToken cancellationToken) =>
            Task.FromResult<AppearanceLocation?>(null);

        public Task<bool> RemoveAsync(string packageId, string packageVersion, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task MarkLastKnownGoodAsync(PortraitSelection selection, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeInstaller : IPackInstaller
    {
        public int InstallCount { get; private set; }
        public Task<PackInstallResult> InstallAsync(string archivePath, CancellationToken cancellationToken)
        {
            InstallCount++;
            return Task.FromResult(new PackInstallResult(false, null,
                new PackFailure(PackErrorCode.PackageArchiveInvalid, "synthetic installer failure")));
        }
    }

    private sealed class FakePortraitController : IPortraitController
    {
        public List<PortraitSelection> Activations { get; } = [];
        public Task ActivateAsync(PortraitSelection selection, CancellationToken cancellationToken)
        {
            Activations.Add(selection);
            return Task.CompletedTask;
        }
        public void SetExpression(ExpressionSemantic semantic) { }
        public void SetScale(double scale) { }
        public void ApplyDpi(Dpi2 dpi) { }
    }
}
