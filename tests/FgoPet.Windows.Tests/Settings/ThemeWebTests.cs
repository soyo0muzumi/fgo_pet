using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using FgoPet.App.Settings;
using FgoPet.App.Theming;
using FgoPet.Character.Settings;
using FgoPet.Core.Settings;
using FgoPet.Extensibility;
using FgoPet.UiFoundation.Theming;
using FgoPet.UiSdk;
using Microsoft.Web.WebView2.Wpf;
using Xunit;
using Xunit.Abstractions;

namespace FgoPet.Windows.Tests.Settings;

public sealed class ThemeWebTests
{
    private readonly ITestOutputHelper _output;
    private long _requestSequence;

    public ThemeWebTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Owner_selects_each_theme_immediately_and_reports_the_service_snapshot() => StaRunner.Run(() =>
    {
        var settings = new ThemeStore(new ThemeSettings(AppTheme.FgoLight));
        var system = new FakeSystemThemeSource(AppTheme.FgoDark);
        using var service = CreateTheme(settings, system);
        var owner = new ThemeWebPage(service);

        Assert.Equal("Theme", owner.SettingsPageId);
        Assert.Equal("pages/theme.js", owner.ModulePath);
        Assert.Equal(["theme.get", "theme.select"], owner.Commands);

        var systemResult = SendOwner(owner, "theme.select", "{\"pageId\":\"Theme\",\"theme\":\"System\"}");
        Assert.True(systemResult.Success, systemResult.ErrorCode);
        AssertSnapshot(systemResult, AppTheme.System, AppTheme.FgoDark, service.StatusText);
        Assert.Equal(AppTheme.System, settings.Value.Theme);
        Assert.Equal(1, settings.SaveCount);

        var lightResult = SendOwner(owner, "theme.select", "{\"pageId\":\"Theme\",\"theme\":\"FgoLight\"}");
        Assert.True(lightResult.Success, lightResult.ErrorCode);
        AssertSnapshot(lightResult, AppTheme.FgoLight, AppTheme.FgoLight, service.StatusText);
        Assert.Equal(AppTheme.FgoLight, settings.Value.Theme);
        Assert.Equal(2, settings.SaveCount);

        var darkResult = SendOwner(owner, "theme.select", "{\"pageId\":\"Theme\",\"theme\":\"FgoDark\"}");
        Assert.True(darkResult.Success, darkResult.ErrorCode);
        AssertSnapshot(darkResult, AppTheme.FgoDark, AppTheme.FgoDark, service.StatusText);
        Assert.Equal(AppTheme.FgoDark, settings.Value.Theme);
        Assert.Equal(3, settings.SaveCount);

        var read = SendOwner(owner, "theme.get", "{\"pageId\":\"Theme\"}");
        Assert.True(read.Success, read.ErrorCode);
        AssertSnapshot(read, AppTheme.FgoDark, AppTheme.FgoDark, service.StatusText);
    });

    [Fact]
    public void Invalid_or_ambiguous_payloads_are_rejected_before_theme_side_effects() => StaRunner.Run(() =>
    {
        var settings = new ThemeStore(new ThemeSettings(AppTheme.FgoLight));
        using var service = CreateTheme(settings, new FakeSystemThemeSource(AppTheme.FgoDark));
        var owner = new ThemeWebPage(service);
        var loadCount = settings.ResourceLoadCount;
        var status = service.StatusText;
        string[] invalid =
        [
            "{}",
            "{\"pageId\":\"Other\"}",
            "{\"pageId\":1}",
            "{\"pageId\":\"Theme\",\"extra\":true}",
            "{\"pageId\":\"Theme\",\"pageId\":\"Theme\"}",
            "{\"pageId\":\"Theme\",\"theme\":\"ModernGray\"}",
            "{\"pageId\":\"Theme\",\"theme\":1}",
            "{\"pageId\":\"Theme\",\"theme\":\"System\",\"extra\":0}",
            "{\"pageId\":\"Theme\",\"theme\":\"System\",\"theme\":\"System\"}",
        ];

        foreach (var payload in invalid)
        {
            var result = SendOwner(owner, "theme.select", payload);
            Assert.False(result.Success);
            Assert.Equal("SETTINGS_INVALID_INPUT", result.ErrorCode);
        }

        var invalidGet = SendOwner(owner, "theme.get", "{\"pageId\":\"Theme\",\"extra\":true}");
        Assert.False(invalidGet.Success);
        Assert.Equal("SETTINGS_INVALID_INPUT", invalidGet.ErrorCode);
        Assert.Equal(AppTheme.FgoLight, service.SelectedTheme);
        Assert.Equal(AppTheme.FgoLight, service.CurrentTheme);
        Assert.Equal(status, service.StatusText);
        Assert.Equal(loadCount, settings.ResourceLoadCount);
        Assert.Equal(0, settings.SaveCount);
    });

    [Fact]
    public void Cancellation_is_propagated_before_theme_application_or_persistence() => StaRunner.Run(() =>
    {
        var settings = new ThemeStore(new ThemeSettings(AppTheme.FgoLight));
        using var service = CreateTheme(settings, new FakeSystemThemeSource(AppTheme.FgoDark));
        var owner = new ThemeWebPage(service);
        var loadCount = settings.ResourceLoadCount;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var payload = JsonDocument.Parse("{\"pageId\":\"Theme\",\"theme\":\"FgoDark\"}");

        LogBoundary("owner-dispatch", "cancelled", "theme.select", "started");
        Assert.Throws<OperationCanceledException>(() =>
        {
            _ = owner.HandleCommandAsync(new WebSurfaceMessage("theme.select", "cancelled",
                payload.RootElement.Clone()), cancellation.Token);
        });
        LogBoundary("owner-result", "cancelled", "theme.select", "cancelled", "OPERATION_CANCELLED");
        Assert.Equal(AppTheme.FgoLight, service.SelectedTheme);
        Assert.Equal(AppTheme.FgoLight, service.CurrentTheme);
        Assert.Equal(loadCount, settings.ResourceLoadCount);
        Assert.Equal(0, settings.SaveCount);
    });

    [Fact]
    public void Apply_failure_keeps_the_old_selection_and_persistence_failure_returns_the_applied_theme() =>
        StaRunner.Run(() =>
        {
            var settings = new ThemeStore(new ThemeSettings(AppTheme.FgoLight));
            using var service = CreateTheme(settings, new FakeSystemThemeSource(AppTheme.FgoDark),
                failDarkResource: true);
            var owner = new ThemeWebPage(service);

            var rejectedByService = SendOwner(owner, "theme.select",
                "{\"pageId\":\"Theme\",\"theme\":\"FgoDark\"}");
            Assert.True(rejectedByService.Success, rejectedByService.ErrorCode);
            AssertSnapshot(rejectedByService, AppTheme.FgoLight, AppTheme.FgoLight, service.StatusText);
            Assert.Equal(AppTheme.FgoLight, settings.Value.Theme);
            Assert.Equal(0, settings.SaveCount);

            settings.AllowDarkResource = true;
            settings.ThrowOnSave = true;
            var appliedButNotPersisted = SendOwner(owner, "theme.select",
                "{\"pageId\":\"Theme\",\"theme\":\"FgoDark\"}");
            Assert.True(appliedButNotPersisted.Success, appliedButNotPersisted.ErrorCode);
            AssertSnapshot(appliedButNotPersisted, AppTheme.FgoDark, AppTheme.FgoDark, service.StatusText);
            Assert.Equal(AppTheme.FgoLight, settings.Value.Theme);
            Assert.Equal(1, settings.SaveCount);

            settings.ThrowOnSave = false;
            var sameThemeRetry = SendOwner(owner, "theme.select",
                "{\"pageId\":\"Theme\",\"theme\":\"FgoDark\"}");
            Assert.True(sameThemeRetry.Success, sameThemeRetry.ErrorCode);
            AssertSnapshot(sameThemeRetry, AppTheme.FgoDark, AppTheme.FgoDark, service.StatusText);
            Assert.Equal(AppTheme.FgoDark, settings.Value.Theme);
            Assert.Equal(2, settings.SaveCount);
        });

    [Fact]
    public void Each_root_gets_an_independent_owner_that_reads_the_shared_theme_service() => StaRunner.Run(() =>
    {
        var settings = new ThemeStore(new ThemeSettings(AppTheme.FgoLight));
        using var service = CreateTheme(settings, new FakeSystemThemeSource(AppTheme.FgoDark));
        var registered = new ThemeWebPage(service);
        var first = registered.CreateSession();
        var second = registered.CreateSession();

        Assert.NotSame(first, second);
        var changed = SendOwner(first, "theme.select", "{\"pageId\":\"Theme\",\"theme\":\"FgoDark\"}");
        Assert.True(changed.Success, changed.ErrorCode);
        var secondRead = SendOwner(second, "theme.get", "{\"pageId\":\"Theme\"}");
        Assert.True(secondRead.Success, secondRead.ErrorCode);
        AssertSnapshot(secondRead, AppTheme.FgoDark, AppTheme.FgoDark, service.StatusText);
    });

    [Fact]
    public async Task Real_settings_root_renders_only_the_theme_card_tracks_system_changes_and_cleans_up_on_navigation()
    {
        await StaRunner.RunAsync(async () =>
        {
            var themeSettings = new ThemeStore(new ThemeSettings(AppTheme.System));
            var system = new FakeSystemThemeSource(AppTheme.FgoDark);
            using var theme = CreateTheme(themeSettings, system);
            var readGate = new BridgeReadFailureGate { FailReads = true };
            var characterSettings = new CharacterStore(CharacterSettings.Defaults);
            var profile = new UserProfileWebFactory(characterSettings,
                new UserProfileViewModel(characterSettings),
                Path.Combine(Path.GetTempPath(), "fgopet-theme-root-profile-" + Guid.NewGuid().ToString("N")), theme);
            var catalog = new SettingsPageCatalog(
            [
                Page("Theme", "外观", 10),
                Page("UserProfile", "用户资料", 20),
            ]);
            var factory = new SettingsWebRootFactory(catalog,
                [new FaultingThemePage(new ThemeWebPage(theme), readGate), profile],
                Path.Combine(Path.GetTempPath(), "fgopet-theme-root-data-" + Guid.NewGuid().ToString("N")), theme);
            var host = factory.CreateView("Theme");
            var window = new Window
            {
                Content = host,
                Width = 540,
                Height = 600,
                ShowInTaskbar = false,
                Left = -10000,
                Top = -10000,
            };

            try
            {
                window.Show();
                LogBoundary("webview-start", "theme-root", "settings.navigate", "started");
                await WaitUntil(() => host.State == WebSurfaceState.Ready);
                var web = Assert.Single(host.Children.OfType<WebView2>());
                await WaitUntil(async () => await Script(web,
                    "document.querySelector('.theme-page')?.dataset.state === 'read-error'") == "true");
                Assert.Equal("true", await Script(web,
                    "Array.from(document.querySelectorAll('input[name=theme-choice]')).every(input => input.disabled)"));
                Assert.Equal("false", await Script(web, "document.querySelector('[data-retry]').hidden"));
                readGate.FailReads = false;
                await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('[data-retry]').click()");
                await WaitUntil(async () => await Script(web,
                    "document.querySelector('.theme-page')?.dataset.state === 'ready'") == "true");
                LogBoundary("webview-ready", "theme-root", "theme.get", "success");
                Assert.Equal("1", await Script(web, "document.querySelectorAll('main h1#page-title').length"));
                Assert.Equal("0", await Script(web, "document.querySelectorAll('.theme-page h1').length"));
                Assert.Equal("System", await Script(web,
                    "document.querySelector('input[name=theme-choice]:checked')?.value ?? ''"));
                Assert.Contains("FGO Dark", await Script(web, "document.querySelector('[data-theme-effective]').textContent"));

                await web.CoreWebView2.ExecuteScriptAsync(
                    "document.querySelector('input[name=theme-choice][value=FgoDark]').click()");
                LogBoundary("webview-selection", "theme-root", "theme.select", "started");
                await WaitUntil(() => themeSettings.Value.Theme == AppTheme.FgoDark);
                await WaitUntil(async () => await Script(web,
                    "document.querySelector('input[name=theme-choice]:checked')?.value ?? ''") == "FgoDark");
                LogBoundary("webview-selection", "theme-root", "theme.select", "success");

                var savesBeforeSystemChange = themeSettings.SaveCount;
                var statusBeforeSystemChange = theme.StatusText;
                theme.Select(AppTheme.System);
                system.Change(AppTheme.FgoLight);
                await WaitUntil(() => theme.CurrentTheme == AppTheme.FgoLight);
                await WaitUntil(async () => await Script(web,
                    "document.querySelector('input[name=theme-choice]:checked')?.value ?? ''") == "System");
                Assert.Contains("FGO Light", await Script(web,
                    "document.querySelector('[data-theme-effective]').textContent"));
                Assert.Equal(savesBeforeSystemChange + 1, themeSettings.SaveCount);
                Assert.NotEqual(statusBeforeSystemChange, theme.StatusText);
                LogBoundary("theme-broadcast", "theme-root", "theme.changed", "success");

                window.Width = 320;
                var viewport = 0;
                await WaitUntil(async () => int.TryParse(await Script(web,
                    "String(document.documentElement.clientWidth)"), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out viewport) && viewport is >= 260 and <= 320);
                Assert.Equal("true", await Script(web,
                    "document.documentElement.scrollWidth <= document.documentElement.clientWidth"));
                Assert.Equal("true", await Script(web,
                    "document.querySelector('main').scrollWidth <= document.querySelector('main').clientWidth"));

                await web.CoreWebView2.ExecuteScriptAsync(
                    "document.querySelector('[data-page-id=UserProfile]').click()");
                LogBoundary("webview-navigation", "theme-root", "settings.navigate", "started");
                await WaitUntil(async () => await Script(web,
                    "document.querySelector('#profile-display-name') !== null") == "true");
                await WaitUntil(async () => await Script(web,
                    "document.querySelector('link[data-settings-owner=theme]') === null") == "true");
                await web.CoreWebView2.ExecuteScriptAsync(
                    "document.querySelector('[data-page-id=Theme]').click()");
                await WaitUntil(async () => await Script(web,
                    "document.querySelector('.theme-page')?.dataset.state === 'ready'") == "true");
                LogBoundary("webview-navigation", "theme-root", "settings.navigate", "success");
                Assert.Equal("1", await Script(web, "document.querySelectorAll('main h1#page-title').length"));
            }
            finally
            {
                window.Close();
            }

            await WaitUntil(() => host.State == WebSurfaceState.Closed);

            using var closedSession = factory.CreateSession("Theme");
            closedSession.Dispose();
            var beforeClosedCommand = themeSettings.SaveCount;
            var afterClose = await Send(closedSession, "theme.select",
                "{\"pageId\":\"Theme\",\"theme\":\"FgoDark\"}");
            Assert.False(afterClose.Success);
            Assert.Equal("WEB_SURFACE_CLOSED", afterClose.ErrorCode);
            Assert.Equal(beforeClosedCommand, themeSettings.SaveCount);
        });
    }

    [Fact]
    public async Task Built_outputs_include_theme_assets_at_the_root_pages_path()
    {
        var destination = Path.Combine(AppContext.BaseDirectory, "Desktop", "ui", "settings", "root", "pages");
        var script = await File.ReadAllTextAsync(Path.Combine(destination, "theme.js"));
        var style = await File.ReadAllTextAsync(Path.Combine(destination, "theme.css"));
        Assert.Contains("theme.select", script);
        Assert.Contains("data-theme-status", script);
        Assert.Contains(".theme-page", style);
    }

    private static void AssertSnapshot(WebSurfaceCommandResult result, AppTheme selected, AppTheme effective,
        string status)
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result.Payload));
        Assert.Equal(selected.ToString(), json.RootElement.GetProperty("selected").GetString());
        Assert.Equal(effective.ToString(), json.RootElement.GetProperty("effective").GetString());
        Assert.Equal(status, json.RootElement.GetProperty("statusText").GetString());
    }

    private static ThemeService CreateTheme(ThemeStore store, FakeSystemThemeSource system,
        bool failDarkResource = false)
    {
        var service = new ThemeService(store, new ResourceDictionary(), selected =>
        {
            store.ResourceLoadCount++;
            if (selected == AppTheme.FgoDark && failDarkResource && !store.AllowDarkResource)
                throw new InvalidOperationException("synthetic theme resource failure");
            return ThemeService.CreateTestDictionary(selected);
        }, system);
        service.Initialize();
        return service;
    }

    private static SettingsPageViewFactory Page(string id, string title, int order) =>
        new(id, _ => throw new InvalidOperationException("Theme is rendered by the Web root."),
            title, group: "测试", order: order);

    private WebSurfaceCommandResult SendOwner(ISettingsWebPage owner, string command, string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var requestId = NextRequestId();
        LogBoundary("owner-dispatch", requestId, command, "started");
        try
        {
            var result = owner.HandleCommandAsync(new WebSurfaceMessage(command, requestId,
                document.RootElement.Clone()), CancellationToken.None).AsTask().GetAwaiter().GetResult();
            LogBoundary("owner-result", requestId, command,
                result.Success ? "success" : "error", result.ErrorCode);
            return result;
        }
        catch (OperationCanceledException)
        {
            LogBoundary("owner-result", requestId, command, "cancelled", "OPERATION_CANCELLED");
            throw;
        }
        catch (Exception)
        {
            LogBoundary("owner-result", requestId, command, "error", "UNEXPECTED");
            throw;
        }
    }

    private async Task<WebSurfaceCommandResult> Send(SettingsWebRootFactory.SettingsWebRootSession session,
        string command, string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var requestId = NextRequestId();
        LogBoundary("root-dispatch", requestId, command, "started");
        var result = await session.HandleCommandAsync(new WebSurfaceMessage(command, requestId,
            document.RootElement.Clone()), CancellationToken.None);
        LogBoundary("root-result", requestId, command,
            result.Success ? "success" : "error", result.ErrorCode);
        return result;
    }

    private string NextRequestId() =>
        $"theme-web-{Interlocked.Increment(ref _requestSequence).ToString(CultureInfo.InvariantCulture)}";

    private void LogBoundary(string phase, string requestId, string command, string outcome,
        string? errorCode = null) => _output.WriteLine(JsonSerializer.Serialize(new
        {
            time = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            phase,
            requestId,
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
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(25);
        Assert.True(condition(), "The Theme root condition did not settle before the deadline.");
    }

    private static async Task WaitUntil(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!await condition() && DateTime.UtcNow < deadline) await Task.Delay(25);
        Assert.True(await condition(), "The Theme WebView did not update before the deadline.");
    }

    private sealed class ThemeStore(ThemeSettings initial) : IThemeSettingsStore
    {
        public ThemeSettings Value { get; private set; } = initial;
        public int SaveCount { get; private set; }
        public int ResourceLoadCount { get; set; }
        public bool ThrowOnSave { get; set; }
        public bool AllowDarkResource { get; set; }
        public ThemeSettings Load() => Value;
        public void Save(ThemeSettings settings)
        {
            SaveCount++;
            if (ThrowOnSave) throw new IOException("synthetic save failure");
            Value = settings;
        }
    }

    private sealed class CharacterStore(CharacterSettings initial) : ICharacterSettingsStore
    {
        public CharacterSettings Value { get; private set; } = initial;
        public CharacterSettings Load() => Value;
        public void Save(CharacterSettings settings) => Value = settings;
    }

    private sealed class FakeSystemThemeSource(AppTheme initial) : ISystemThemeSource
    {
        public AppTheme Value { get; private set; } = initial;
        public event EventHandler? Changed;
        public AppTheme ReadTheme() => Value;
        public void Change(AppTheme theme)
        {
            Value = theme;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class BridgeReadFailureGate
    {
        public bool FailReads { get; set; }
    }

    private sealed class FaultingThemePage(ISettingsWebPage inner, BridgeReadFailureGate gate) : ISettingsWebPage
    {
        public string SettingsPageId => inner.SettingsPageId;
        public string ModulePath => inner.ModulePath;
        public IReadOnlyList<string> Commands => inner.Commands;
        public ISettingsWebPage CreateSession() => new FaultingThemePage(inner.CreateSession(), gate);

        public ValueTask<WebSurfaceCommandResult> HandleCommandAsync(WebSurfaceMessage message,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (gate.FailReads && message.Type == "theme.get")
                return ValueTask.FromResult(new WebSurfaceCommandResult(false, ErrorCode: "SETTINGS_UNAVAILABLE"));
            return inner.HandleCommandAsync(message, cancellationToken);
        }
    }
}
