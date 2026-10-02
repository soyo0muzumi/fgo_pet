using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using FgoPet.App.Servants;
using FgoPet.App.Settings;
using FgoPet.App.Theming;
using FgoPet.Character.Settings;
using FgoPet.Core.Geometry;
using FgoPet.Core.Packs;
using FgoPet.Core.Portraits;
using FgoPet.Core.Settings;
using FgoPet.Extensibility;
using FgoPet.UiFoundation.Theming;
using FgoPet.UiSdk;
using Microsoft.Web.WebView2.Wpf;
using Xunit;

namespace FgoPet.Windows.Tests.Settings;

public sealed class PersonalizationWebTests
{
    [Fact]
    public async Task Real_settings_root_switches_pages_persists_personalization_and_broadcasts_theme()
    {
        await StaRunner.RunAsync(async () =>
        {
            var previewPath = WriteSmallPng();
            var privateFileName = Path.GetFileName(previewPath);
            var settings = new Store(CharacterSettings.Defaults with
            {
                UserProfile = new UserProfile("原名称"),
                Scale = 0.75,
                Topmost = false,
                AutoCollapseExpandedPanel = false,
            });
            var themeStore = new ThemeStore(new ThemeSettings(AppTheme.FgoLight));
            using var theme = CreateTheme(themeStore);
            var portrait = new PortraitController();
            var repository = new Repository(new InstalledServant(
                "test.package", "test.servant", "测试从者", previewPath, "unverified",
                [new ServantAppearance("default", "1.0.0", Path.GetTempPath(), previewPath)])
            { PackageVersion = "1.0.0" });
            var installer = new Installer();
            var library = new ServantLibraryViewModel(repository, installer, portrait, settings);
            var personalization = new PersonalizationWebPage(settings, portrait, theme, library);
            var profile = new UserProfileWebFactory(settings, new UserProfileViewModel(settings),
                Path.Combine(Path.GetTempPath(), "fgopet-personalization-root-" + Guid.NewGuid().ToString("N")), theme);
            var catalog = new SettingsPageCatalog(
            [
                Page("Personalization", "个性化", 10),
                Page("UserProfile", "用户资料", 20),
            ]);
            var factory = new SettingsWebRootFactory(catalog, [personalization, profile],
                Path.Combine(Path.GetTempPath(), "fgopet-personalization-root-data-" + Guid.NewGuid().ToString("N")), theme);
            var host = factory.CreateView();
            var window = new Window
            {
                Content = host,
                Width = 540,
                Height = 760,
                ShowInTaskbar = false,
                Left = -10000,
                Top = -10000,
            };

            try
            {
                window.Show();
                await WaitUntil(() => host.State == WebSurfaceState.Ready);
                var web = Assert.Single(host.Children.OfType<WebView2>());
                await WaitUntil(async () => await Script(web,
                    "document.querySelector('[data-role-name]')?.textContent ?? ''") == "测试从者");

                Assert.Equal("1", await web.CoreWebView2.ExecuteScriptAsync(
                    "document.querySelectorAll('main h1#page-title').length"));
                Assert.Equal("个性化", await Script(web, "document.querySelector('#page-title').textContent"));
                Assert.Equal("true", await web.CoreWebView2.ExecuteScriptAsync(
                    "document.querySelector('[data-role-image]').src.startsWith('data:image/png;base64,')"));
                Assert.Equal("false", await web.CoreWebView2.ExecuteScriptAsync(
                    $"document.body.innerText.includes({JsonSerializer.Serialize(privateFileName)})"));
                Assert.Equal(1, repository.ListCount);
                Assert.Equal(0, installer.InstallCount);
                Assert.Equal(0, portrait.ActivateCount);

                window.Width = 320;
                await WaitUntil(async () =>
                {
                    var widthText = await Script(web, "String(document.documentElement.clientWidth)");
                    return int.TryParse(widthText, out var width) && width is >= 260 and <= 320;
                });
                Assert.Equal("true", await web.CoreWebView2.ExecuteScriptAsync(
                    "document.documentElement.scrollWidth <= document.documentElement.clientWidth"));
                Assert.Equal("true", await web.CoreWebView2.ExecuteScriptAsync(
                    "document.querySelector('main').scrollWidth <= document.querySelector('main').clientWidth"));

                await web.CoreWebView2.ExecuteScriptAsync(
                    "document.querySelector('[data-page-id=UserProfile]').click()");
                await WaitUntil(async () => await Script(web,
                    "document.querySelector('#profile-display-name')?.value ?? ''") == "原名称");
                Assert.Equal("用户资料", await Script(web, "document.querySelector('#page-title').textContent"));
                await web.CoreWebView2.ExecuteScriptAsync(
                    "(() => { const input=document.querySelector('#profile-display-name'); input.value='跨页保存'; input.dispatchEvent(new Event('input',{bubbles:true})); document.querySelector('[data-action=save]').click(); })()");
                await WaitUntil(() => settings.Value.UserProfile?.DisplayName == "跨页保存");

                await web.CoreWebView2.ExecuteScriptAsync(
                    "document.querySelector('[data-page-id=Personalization]').click()");
                await WaitUntil(async () => await Script(web,
                    "document.querySelector('[data-role-name]')?.textContent ?? ''") == "测试从者");
                Assert.Equal("个性化", await Script(web, "document.querySelector('#page-title').textContent"));
                Assert.Equal("1", await web.CoreWebView2.ExecuteScriptAsync(
                    "document.querySelectorAll('main h1#page-title').length"));

                await web.CoreWebView2.ExecuteScriptAsync(
                    "document.querySelector('input[name=\"pref-scale\"][value=\"0.6\"]').click()");
                await WaitUntil(() => settings.Value.Scale == 0.6);
                Assert.Contains(0.6, portrait.Scales);
                await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('[data-field=topmost]').click()");
                await WaitUntil(() => settings.Value.Topmost);
                await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('[data-field=autoCollapseExpandedPanel]').click()");
                await WaitUntil(() => settings.Value.AutoCollapseExpandedPanel);

                await web.CoreWebView2.ExecuteScriptAsync(
                    "document.querySelector('input[name=pref-theme][value=FgoDark]').click()");
                await WaitUntil(() => themeStore.Value.Theme == AppTheme.FgoDark);
                await WaitUntil(() => host.AckedThemeVersion >= 2);
                Assert.Equal("#23242B", await Script(web,
                    "getComputedStyle(document.documentElement).getPropertyValue('--surface').trim()"));
                await WaitUntil(async () => await web.CoreWebView2.ExecuteScriptAsync(
                    "document.querySelector('input[name=pref-theme][value=FgoDark]').checked") == "true");

                await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('[data-reset]').click()");
                await WaitUntil(() => settings.Value.Scale == CharacterSettings.Defaults.Scale
                    && settings.Value.Topmost == CharacterSettings.Defaults.Topmost
                    && settings.Value.AutoCollapseExpandedPanel == CharacterSettings.Defaults.AutoCollapseExpandedPanel);
                Assert.Equal("跨页保存", settings.Value.UserProfile?.DisplayName);
                Assert.Equal(AppTheme.FgoDark, themeStore.Value.Theme);

                window.Close();
                await WaitUntil(() => host.State == WebSurfaceState.Closed);
            }
            finally
            {
                if (window.IsVisible) window.Close();
                if (File.Exists(previewPath)) File.Delete(previewPath);
            }

            using var session = factory.CreateSession("Personalization");
            var beforeClose = await Send(session, "personalization.get",
                "{\"pageId\":\"Personalization\"}");
            Assert.True(beforeClose.Success);
            var writesBeforeClose = settings.SaveCount;
            session.Dispose();
            var afterClose = await Send(session, "personalization.set",
                "{\"pageId\":\"Personalization\",\"field\":\"topmost\",\"value\":false}");
            Assert.False(afterClose.Success);
            Assert.Equal("WEB_SURFACE_CLOSED", afterClose.ErrorCode);
            Assert.Equal(writesBeforeClose, settings.SaveCount);
        });
    }

    [Fact]
    public async Task Concurrent_personalization_and_profile_owner_sessions_preserve_each_others_fields()
    {
        var store = new CrossPageRaceStore(CharacterSettings.Defaults with
        {
            UserProfile = new UserProfile("旧名称"),
        });
        using var theme = CreateTheme(new ThemeStore(new ThemeSettings(AppTheme.FgoLight)));
        var personalization = new PersonalizationWebPage(store, null, theme).CreateSession();
        var profileFactory = new UserProfileWebFactory(store, new UserProfileViewModel(store),
            Path.Combine(Path.GetTempPath(), "fgopet-cross-page-race-" + Guid.NewGuid().ToString("N")), theme);
        var profile = profileFactory.CreateSession();

        var personalizationTask = Task.Factory.StartNew(() =>
        {
            using var operation = store.EnterOperation("personalization");
            return SendOwner(personalization, "personalization.set",
                "{\"pageId\":\"Personalization\",\"field\":\"scale\",\"value\":0.75}");
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        try
        {
            Assert.True(await Task.Run(() => store.PersonalizationWriteLoadEntered.Wait(TimeSpan.FromSeconds(5))),
                "Personalization did not reach the load immediately before its write.");

            var profileTask = Task.Factory.StartNew(() =>
            {
                using var operation = store.EnterOperation("profile");
                return SendOwner(profile, "saveProfile", "{\"displayName\":\"新名称\"}");
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

            // Without a store-wide gate, Profile completes from the stale snapshot captured above.
            // With the shared gate, it waits for Personalization to commit and then patches that value.
            await Task.WhenAny(profileTask, Task.Delay(500));
            store.ResumePersonalizationWriteLoad();

            var results = await Task.WhenAll(personalizationTask, profileTask).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.All(results, result => Assert.True(result.Success, result.ErrorCode));
            Assert.Equal(0.75, store.Value.Scale);
            Assert.Equal("新名称", store.Value.UserProfile?.DisplayName);
        }
        finally
        {
            store.ResumePersonalizationWriteLoad();
        }
    }

    [Fact]
    public async Task Built_outputs_include_the_personalization_module_under_the_root_pages_path()
    {
        var destination = Path.Combine(AppContext.BaseDirectory, "Desktop", "ui", "settings", "root", "pages");
        var scriptPath = Path.Combine(destination, "personalization.js");
        var stylePath = Path.Combine(destination, "personalization.css");
        Assert.True(File.Exists(scriptPath), scriptPath);
        Assert.True(File.Exists(stylePath), stylePath);
        Assert.Contains("personalization.setTheme", await File.ReadAllTextAsync(scriptPath));
        Assert.Contains("pref-page", await File.ReadAllTextAsync(stylePath));
    }

    private static SettingsPageViewFactory Page(string id, string title, int order) =>
        new(id, _ => throw new InvalidOperationException("The Web root owns this settings presentation."),
            title, group: "测试", order: order);

    private static ThemeService CreateTheme(ThemeStore store)
    {
        var service = new ThemeService(store, new ResourceDictionary(), selected =>
        {
            var dark = selected == AppTheme.FgoDark;
            var content = dark ? Color.FromRgb(35, 36, 43) : Color.FromRgb(255, 255, 255);
            var page = dark ? Color.FromRgb(24, 25, 31) : Color.FromRgb(241, 240, 243);
            return new ResourceDictionary
            {
                ["Semantic.ContentColor"] = content,
                ["Semantic.SubtleColor"] = page,
                ["Semantic.PrimaryTextColor"] = dark ? Colors.White : Colors.Black,
                ["Semantic.SecondaryTextColor"] = dark ? Colors.LightGray : Colors.DimGray,
                ["Semantic.BorderDecorativeColor"] = dark ? Colors.DarkGray : Colors.LightGray,
                ["Semantic.PrimaryActionColor"] = dark ? Color.FromRgb(195, 168, 255) : Color.FromRgb(112, 80, 184),
                ["Semantic.OnPrimaryActionColor"] = Colors.White,
                ["DangerColor"] = Colors.Red,
            };
        }, new SystemTheme());
        service.Initialize();
        return service;
    }

    private static string WriteSmallPng()
    {
        var path = Path.Combine(Path.GetTempPath(), "fgopet-preview-private-" + Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllBytes(path, Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/smkAAAAASUVORK5CYII="));
        return path;
    }

    private static async Task<string> Script(WebView2 web, string code) =>
        JsonSerializer.Deserialize<string>(await web.CoreWebView2.ExecuteScriptAsync(code)) ?? string.Empty;

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(25);
        Assert.True(condition(), "The Personalization root condition did not settle before the deadline.");
    }

    private static async Task WaitUntil(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!await condition() && DateTime.UtcNow < deadline) await Task.Delay(25);
        Assert.True(await condition(), "The Personalization WebView did not update before the deadline.");
    }

    private static Task<WebSurfaceCommandResult> Send(SettingsWebRootFactory.SettingsWebRootSession session,
        string command, string payload) => session.HandleCommandAsync(
        new WebSurfaceMessage(command, "personalization-web-test", JsonDocument.Parse(payload).RootElement.Clone()),
        CancellationToken.None).AsTask();

    private static WebSurfaceCommandResult SendOwner(ISettingsWebPage page, string command, string payload)
    {
        using var document = JsonDocument.Parse(payload);
        return page.HandleCommandAsync(new WebSurfaceMessage(command, "cross-page-race-test",
            document.RootElement.Clone()), CancellationToken.None).AsTask().GetAwaiter().GetResult();
    }

    private sealed class Store(CharacterSettings initial) : ICharacterSettingsStore
    {
        public CharacterSettings Value { get; private set; } = initial;
        public int SaveCount { get; private set; }
        public CharacterSettings Load() => Value;
        public void Save(CharacterSettings settings) { SaveCount++; Value = settings; }
    }

    private sealed class CrossPageRaceStore(CharacterSettings initial) : ICharacterSettingsStore
    {
        private readonly object _stateGate = new();
        private readonly AsyncLocal<string?> _operation = new();
        private readonly Dictionary<string, int> _loadCounts = new(StringComparer.Ordinal);
        private readonly ManualResetEventSlim _personalizationWriteLoadRelease = new();
        public ManualResetEventSlim PersonalizationWriteLoadEntered { get; } = new();
        public CharacterSettings Value { get { lock (_stateGate) return _value; } }
        private CharacterSettings _value = initial;

        public CharacterSettings Load()
        {
            CharacterSettings snapshot;
            var operation = _operation.Value;
            int count;
            lock (_stateGate)
            {
                snapshot = _value;
                if (operation is not null)
                {
                    _loadCounts.TryGetValue(operation, out count);
                    _loadCounts[operation] = ++count;
                }
                else
                {
                    count = 0;
                }
            }

            if (operation == "personalization" && count == 2)
            {
                PersonalizationWriteLoadEntered.Set();
                if (!_personalizationWriteLoadRelease.Wait(TimeSpan.FromSeconds(15)))
                    throw new TimeoutException("Timed out waiting to release the Personalization write load.");
            }

            return snapshot;
        }

        public void Save(CharacterSettings settings)
        {
            lock (_stateGate)
                _value = settings;
        }

        public IDisposable EnterOperation(string operation)
        {
            var previous = _operation.Value;
            _operation.Value = operation;
            return new OperationScope(_operation, previous);
        }

        public void ResumePersonalizationWriteLoad() => _personalizationWriteLoadRelease.Set();

        private sealed class OperationScope(AsyncLocal<string?> operation, string? previous) : IDisposable
        {
            public void Dispose() => operation.Value = previous;
        }
    }

    private sealed class ThemeStore(ThemeSettings initial) : IThemeSettingsStore
    {
        public ThemeSettings Value { get; private set; } = initial;
        public ThemeSettings Load() => Value;
        public void Save(ThemeSettings settings) => Value = settings;
    }

    private sealed class SystemTheme : ISystemThemeSource
    {
        public event EventHandler? Changed { add { } remove { } }
        public AppTheme ReadTheme() => AppTheme.FgoDark;
    }

    private sealed class PortraitController : IPortraitController
    {
        public List<double> Scales { get; } = [];
        public int ActivateCount { get; private set; }
        public Task ActivateAsync(PortraitSelection selection, CancellationToken cancellationToken)
        {
            ActivateCount++;
            return Task.CompletedTask;
        }
        public void SetExpression(ExpressionSemantic semantic) { }
        public void SetScale(double scale) => Scales.Add(scale);
        public void ApplyDpi(Dpi2 dpi) { }
    }

    private sealed class Repository(InstalledServant servant) : IArtPackageRepository
    {
        public int ListCount { get; private set; }
        public Task<PackCatalog> ScanAsync(CancellationToken cancellationToken) => Task.FromResult(new PackCatalog([]));
        public Task<IReadOnlyList<InstalledServant>> ListServantsAsync(CancellationToken cancellationToken)
        {
            ListCount++;
            return Task.FromResult<IReadOnlyList<InstalledServant>>([servant]);
        }
        public Task<AppearanceLocation?> GetAppearanceAsync(PortraitSelection selection, CancellationToken cancellationToken) => Task.FromResult<AppearanceLocation?>(null);
        public Task<AppearanceLocation?> ResolveStartupSelectionAsync(PortraitSelection? requested, CancellationToken cancellationToken) => Task.FromResult<AppearanceLocation?>(null);
        public Task<bool> RemoveAsync(string packageId, string packageVersion, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task MarkLastKnownGoodAsync(PortraitSelection selection, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class Installer : IPackInstaller
    {
        public int InstallCount { get; private set; }
        public Task<PackInstallResult> InstallAsync(string archivePath, CancellationToken cancellationToken)
        {
            InstallCount++;
            return Task.FromResult(new PackInstallResult(false, null, null));
        }
    }
}
