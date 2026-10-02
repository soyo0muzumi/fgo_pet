using System.IO;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using FgoPet.App.Settings;
using FgoPet.Character.Settings;
using FgoPet.Core.Settings;
using FgoPet.Extensibility;
using FgoPet.UiSdk;
using Microsoft.Web.WebView2.Wpf;
using Xunit;

namespace FgoPet.Windows.Tests.Settings;

public sealed class SettingsWebRootTests
{
    [Fact]
    public async Task Catalog_includes_unmigrated_metadata_but_only_migrated_pages_can_be_selected()
    {
        using var modules = new ModuleRoot();
        var catalog = Catalog(
            Page("UnavailableFirst", order: 1),
            Page("UserProfile", order: 10),
            Page("MigratedSecond", order: 20));
        var profile = new FakePage("UserProfile", "pages/profile.js", ["readProfile"]);
        var second = new FakePage("MigratedSecond", "pages/second.js", ["readSecond"]);
        var factory = Factory(catalog, modules, profile, second);
        using var session = factory.CreateSession();

        var catalogResult = await Send(session, "settings.getCatalog", "{}");
        Assert.True(catalogResult.Success);
        using var metadata = JsonDocument.Parse(JsonSerializer.Serialize(catalogResult.Payload));
        var pages = metadata.RootElement.GetProperty("pages").EnumerateArray().ToArray();
        Assert.Equal(["UnavailableFirst", "UserProfile", "MigratedSecond"],
            pages.Select(page => page.GetProperty("id").GetString()).ToArray());
        Assert.False(pages[0].GetProperty("available").GetBoolean());
        Assert.Null(pages[0].GetProperty("module").GetString());
        Assert.True(pages[1].GetProperty("available").GetBoolean());
        Assert.Equal("pages/profile.js", pages[1].GetProperty("module").GetString());
        Assert.Equal("UserProfile", metadata.RootElement.GetProperty("selectedPageId").GetString());

        var rejected = await Send(session, "settings.navigate", "{\"pageId\":\"UnavailableFirst\"}");
        Assert.False(rejected.Success);
        Assert.Equal("SETTINGS_PAGE_UNAVAILABLE", rejected.ErrorCode);
        var after = await Send(session, "settings.getCatalog", "{}");
        using var afterJson = JsonDocument.Parse(JsonSerializer.Serialize(after.Payload));
        Assert.Equal("UserProfile", afterJson.RootElement.GetProperty("selectedPageId").GetString());
    }

    [Fact]
    public async Task Initial_page_follows_catalog_order_without_user_profile_special_case()
    {
        using var modules = new ModuleRoot();
        var catalog = Catalog(Page("MigratedFirst", order: 1), Page("UserProfile", order: 10));
        var factory = Factory(catalog, modules,
            new FakePage("UserProfile", "pages/profile.js", ["profile.read"]),
            new FakePage("MigratedFirst", "pages/first.js", ["first.read"]));

        using var session = factory.CreateSession();
        var result = await Send(session, "settings.getCatalog", "{}");
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result.Payload));
        Assert.Equal("MigratedFirst", json.RootElement.GetProperty("selectedPageId").GetString());

        using var noPages = Factory(Catalog(Page("UserProfile", order: 1)), modules).CreateSession();
        var emptyResult = await Send(noPages, "settings.getCatalog", "{}");
        using var emptyJson = JsonDocument.Parse(JsonSerializer.Serialize(emptyResult.Payload));
        Assert.Equal(JsonValueKind.Null, emptyJson.RootElement.GetProperty("selectedPageId").ValueKind);
    }

    [Fact]
    public async Task Each_root_session_keeps_its_selected_page_independent()
    {
        using var modules = new ModuleRoot();
        var factory = Factory(Catalog(Page("UserProfile", order: 1), Page("Theme", order: 2)), modules,
            new FakePage("UserProfile", "pages/profile.js", ["profile.read"]),
            new FakePage("Theme", "pages/theme.js", ["theme.read"]));
        using var first = factory.CreateSession();
        using var second = factory.CreateSession();

        Assert.True((await Send(first, "settings.navigate", "{\"pageId\":\"Theme\"}")).Success);
        Assert.Equal("Theme", await SelectedPage(first));
        Assert.Equal("UserProfile", await SelectedPage(second));
    }

    [Fact]
    public async Task Owner_request_requires_the_current_owner_page_id()
    {
        using var modules = new ModuleRoot();
        var firstPage = new FakePage("UserProfile", "pages/profile.js", ["profile.read"]);
        var secondPage = new FakePage("Theme", "pages/theme.js", ["theme.read"]);
        using var session = Factory(Catalog(Page("UserProfile", order: 1), Page("Theme", order: 2)),
            modules, firstPage, secondPage).CreateSession();

        Assert.Equal("SETTINGS_PAGE_MISMATCH",
            (await Send(session, "profile.read", "{\"pageId\":\"Theme\"}")).ErrorCode);
        Assert.Equal(0, firstPage.CallCount);
        Assert.Equal(0, secondPage.CallCount);

        Assert.True((await Send(session, "settings.navigate", "{\"pageId\":\"Theme\"}")).Success);
        Assert.Equal("SETTINGS_PAGE_MISMATCH",
            (await Send(session, "profile.read", "{\"pageId\":\"Theme\"}")).ErrorCode);
        Assert.Equal(0, firstPage.CallCount);
        Assert.Equal(0, secondPage.CallCount);
        Assert.True((await Send(session, "theme.read", "{\"pageId\":\"Theme\"}")).Success);
        Assert.Equal(1, secondPage.CallCount);
    }

    [Fact]
    public async Task Owner_registration_captures_immutable_page_id_module_and_commands()
    {
        using var modules = new ModuleRoot();
        var page = new FakePage("UserProfile", "pages/profile.js", ["profile.read"]);
        using var session = Factory(Catalog(Page("UserProfile", order: 1)), modules, page).CreateSession();

        page.SettingsPageId = "Theme";
        page.ModulePath = "pages/theme.js";
        page.CommandsMutable.Clear();
        page.CommandsMutable.Add("theme.read");

        Assert.Equal("SETTINGS_UNKNOWN_COMMAND", (await Send(session, "theme.read", "{\"pageId\":\"UserProfile\"}")).ErrorCode);
        Assert.True((await Send(session, "profile.read", "{\"pageId\":\"UserProfile\"}")).Success);
        var catalogResult = await Send(session, "settings.getCatalog", "{}");
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(catalogResult.Payload));
        var registered = Assert.Single(json.RootElement.GetProperty("pages").EnumerateArray());
        Assert.Equal("UserProfile", registered.GetProperty("id").GetString());
        Assert.Equal("pages/profile.js", registered.GetProperty("module").GetString());
    }

    [Theory]
    [InlineData("ready")]
    [InlineData("theme.ack")]
    [InlineData("settings.getCatalog")]
    [InlineData("settings.navigate")]
    public void Owner_cannot_register_a_host_or_root_reserved_command(string command)
    {
        using var modules = new ModuleRoot();
        var page = new FakePage("UserProfile", "pages/profile.js", [command]);
        Assert.Throws<PluginValidationException>(() => Factory(
            Catalog(Page("UserProfile", order: 1)), modules, page));
    }

    [Theory]
    [InlineData("../outside.js")]
    [InlineData("pages/../outside.js")]
    [InlineData("pages/nested/profile.js")]
    [InlineData("https://example.invalid/profile.js")]
    public void Owner_module_must_be_a_flat_local_pages_javascript_path(string modulePath)
    {
        using var modules = new ModuleRoot();
        var page = new FakePage("UserProfile", modulePath, ["profile.read"]);
        Assert.Throws<PluginValidationException>(() => Factory(
            Catalog(Page("UserProfile", order: 1)), modules, page));
    }

    [Theory]
    [InlineData("bad/page")]
    public void Owner_page_id_must_be_a_safe_single_catalog_identifier(string pageId)
    {
        using var modules = new ModuleRoot();
        var catalog = Catalog(Page(pageId, order: 1));
        var owner = new FakePage(pageId, "pages/profile.js", ["profile.read"]);
        Assert.Throws<PluginValidationException>(() => Factory(catalog, modules, owner));
    }

    [Fact]
    public void Catalog_metadata_page_ids_are_validated_before_they_reach_web()
    {
        using var modules = new ModuleRoot();
        Assert.Throws<PluginValidationException>(() => Factory(Catalog(Page("bad/page", order: 1)), modules));
    }

    [Fact]
    public void Duplicate_owner_commands_are_rejected()
    {
        using var modules = new ModuleRoot();
        var catalog = Catalog(Page("UserProfile", order: 1), Page("Theme", order: 2));
        Assert.Throws<PluginValidationException>(() => Factory(catalog, modules,
            new FakePage("UserProfile", "pages/profile.js", ["shared.read"]),
            new FakePage("Theme", "pages/theme.js", ["shared.read"])));
    }

    [Fact]
    public void Duplicate_command_within_one_owner_is_rejected()
    {
        using var modules = new ModuleRoot();
        Assert.Throws<PluginValidationException>(() => Factory(
            Catalog(Page("UserProfile", order: 1)), modules,
            new FakePage("UserProfile", "pages/profile.js", ["profile.read", "profile.read"])));
    }

    [Fact]
    public async Task Navigation_cancels_old_owner_before_it_can_commit_and_rejects_its_late_reply()
    {
        using var modules = new ModuleRoot();
        var pending = new PausedOwner("UserProfile", "pages/profile.js", "profile.save");
        var theme = new FakePage("Theme", "pages/theme.js", ["theme.read"]);
        using var session = Factory(Catalog(Page("UserProfile", order: 1), Page("Theme", order: 2)),
            modules, pending, theme).CreateSession();

        var save = Send(session, "profile.save", "{\"pageId\":\"UserProfile\"}");
        await pending.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True((await Send(session, "settings.navigate", "{\"pageId\":\"Theme\"}")).Success);
        Assert.True(pending.ObservedToken!.Value.IsCancellationRequested);
        pending.Continue.TrySetResult();

        var lateResult = await save.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(lateResult.Success);
        Assert.Equal("SETTINGS_PAGE_CHANGED", lateResult.ErrorCode);
        Assert.Equal(0, pending.CommitCount);
        Assert.True((await Send(session, "theme.read", "{\"pageId\":\"Theme\"}")).Success);
    }

    [Fact]
    public async Task Dispose_cancels_pending_owner_and_closes_queued_or_late_work()
    {
        using var modules = new ModuleRoot();
        var pending = new PausedOwner("UserProfile", "pages/profile.js", "profile.save");
        var session = Factory(Catalog(Page("UserProfile", order: 1)), modules, pending).CreateSession();
        var save = Send(session, "profile.save", "{\"pageId\":\"UserProfile\"}");
        await pending.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        session.Dispose();
        Assert.True(pending.ObservedToken!.Value.IsCancellationRequested);
        var queued = await Send(session, "profile.save", "{\"pageId\":\"UserProfile\"}");
        Assert.Equal("WEB_SURFACE_CLOSED", queued.ErrorCode);
        pending.Continue.TrySetResult();

        var lateResult = await save.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(lateResult.Success);
        Assert.Equal("WEB_SURFACE_CLOSED", lateResult.ErrorCode);
        Assert.Equal(0, pending.CommitCount);
    }

    [Fact]
    public async Task Host_cancellation_prevents_commit_before_session_dispose_runs()
    {
        using var modules = new ModuleRoot();
        using var hostLifetime = new CancellationTokenSource();
        var pending = new PausedOwner("UserProfile", "pages/profile.js", "profile.save");
        using var session = Factory(Catalog(Page("UserProfile", order: 1)), modules, pending).CreateSession();
        var save = session.HandleCommandAsync(new WebSurfaceMessage("profile.save", "request-1",
            JsonDocument.Parse("{\"pageId\":\"UserProfile\"}").RootElement.Clone()), hostLifetime.Token).AsTask();
        await pending.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        hostLifetime.Cancel();
        Assert.True(pending.ObservedToken!.Value.IsCancellationRequested);
        Assert.True((await Send(session, "settings.getCatalog", "{}")).Success);
        pending.Continue.TrySetResult();

        var lateResult = await save.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(lateResult.Success);
        Assert.Equal("WEB_SURFACE_CLOSED", lateResult.ErrorCode);
        Assert.Equal(0, pending.CommitCount);
    }

    [Fact]
    public async Task Real_settings_root_reads_saves_cancels_and_resets_profile_in_one_web_heading()
    {
        await StaRunner.RunAsync(async () =>
        {
            var settings = new Store(CharacterSettings.Defaults with
            {
                UserProfile = new UserProfile("旧名称"),
                Scale = 0.72,
            });
            var profileViewModel = new UserProfileViewModel(settings);
            var profilePage = new UserProfileWebFactory(settings, profileViewModel,
                Path.Combine(Path.GetTempPath(), "fgopet-settings-root-test-" + Guid.NewGuid().ToString("N")));
            var catalog = Catalog(
                Page("UserProfile", order: 10, title: "用户资料"),
                Page("Privacy", order: 20));
            var factory = new SettingsWebRootFactory(catalog, [profilePage],
                Path.Combine(Path.GetTempPath(), "fgopet-settings-root-profile-" + Guid.NewGuid().ToString("N")));
            var host = factory.CreateView();
            var window = new Window
            {
                Content = host,
                Width = 540,
                Height = 420,
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
                    "document.querySelector('#profile-display-name')?.value ?? ''") == "旧名称");

                // The shell window hosts the Web root directly, so no visible WPF navigation
                // or WPF page heading can wrap this surface.
                Assert.IsType<WebView2SurfaceHost>(window.Content);
                Assert.Equal("1", await web.CoreWebView2.ExecuteScriptAsync(
                    "document.querySelectorAll('main h1#page-title').length"));
                Assert.Equal("用户资料", await Script(web, "document.querySelector('#page-title').textContent"));

                window.Width = 320;
                var viewport = 0;
                await WaitUntil(async () => int.TryParse(await Script(web,
                    "String(document.documentElement.clientWidth)"), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out viewport) && viewport is >= 260 and <= 320);
                Assert.Equal("true", await web.CoreWebView2.ExecuteScriptAsync(
                    "document.documentElement.scrollWidth <= document.documentElement.clientWidth"));
                Assert.Equal("true", await web.CoreWebView2.ExecuteScriptAsync(
                    "document.querySelector('main').scrollWidth <= document.querySelector('main').clientWidth"));

                await web.CoreWebView2.ExecuteScriptAsync(
                    "(() => { const input=document.querySelector('#profile-display-name'); input.value='未保存'; input.dispatchEvent(new Event('input',{bubbles:true})); document.querySelector('[data-action=cancel]').click(); })()");
                Assert.Equal("旧名称", await Script(web, "document.querySelector('#profile-display-name').value"));
                Assert.Equal("旧名称", settings.Load().UserProfile?.DisplayName);

                await web.CoreWebView2.ExecuteScriptAsync(
                    "(() => { const input=document.querySelector('#profile-display-name'); input.value='新名称'; input.dispatchEvent(new Event('input',{bubbles:true})); document.querySelector('[data-action=save]').click(); })()");
                await WaitUntil(() => settings.Load().UserProfile?.DisplayName == "新名称");
                await WaitUntil(async () => await Script(web,
                    "document.querySelector('#profile-display-name').value") == "新名称");
                Assert.Equal(0.72, settings.Load().Scale);
                Assert.Equal("1", await web.CoreWebView2.ExecuteScriptAsync(
                    "document.querySelectorAll('main h1#page-title').length"));

                await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('[data-action=reset]').click()");
                await WaitUntil(() => settings.Load().UserProfile is null);
                await WaitUntil(async () => await Script(web,
                    "document.querySelector('#profile-display-name').value") == string.Empty);
                Assert.Equal(0.72, settings.Load().Scale);
            }
            finally
            {
                window.Close();
            }

            await WaitUntil(() => host.State == WebSurfaceState.Closed);
        });
    }

    [Fact]
    public async Task User_profile_owner_uses_an_independent_view_model_per_root_session_and_keeps_store_semantics()
    {
        var settings = new Store(CharacterSettings.Defaults with
        {
            UserProfile = new UserProfile("原名称"),
            Scale = 0.72,
        });
        var originalViewModel = new UserProfileViewModel(settings) { DisplayName = "未触及的owner草稿" };
        var profilePage = new UserProfileWebFactory(settings, originalViewModel,
            Path.Combine(Path.GetTempPath(), "fgopet-settings-owner-test"));
        var factory = new SettingsWebRootFactory(Catalog(Page("UserProfile", order: 1)), [profilePage],
            Path.Combine(Path.GetTempPath(), "fgopet-settings-owner-storage"));
        using var first = factory.CreateSession();
        using var second = factory.CreateSession();

        var firstSave = await Send(first, "saveProfile",
            "{\"pageId\":\"UserProfile\",\"displayName\":\"第一个root\"}");
        Assert.True(firstSave.Success);
        Assert.Equal("未触及的owner草稿", originalViewModel.DisplayName);
        Assert.Equal(0.72, settings.Load().Scale);

        var secondSave = await Send(second, "saveProfile",
            "{\"pageId\":\"UserProfile\",\"displayName\":\"第二个root\"}");
        Assert.True(secondSave.Success);
        Assert.Equal("第二个root", settings.Load().UserProfile?.DisplayName);
        Assert.Equal(0.72, settings.Load().Scale);

        var reset = await Send(first, "resetProfile", "{\"pageId\":\"UserProfile\"}");
        Assert.True(reset.Success);
        Assert.Null(settings.Load().UserProfile);
        Assert.Equal(0.72, settings.Load().Scale);
    }

    [Fact]
    public async Task Root_profile_load_failure_is_retryable_after_the_view_and_session_are_created()
    {
        await StaRunner.RunAsync(async () =>
        {
            var settings = new Store(CharacterSettings.Defaults with
            {
                UserProfile = new UserProfile("原名称"),
                Scale = 0.72,
            });
            var originalViewModel = new UserProfileViewModel(settings)
            {
                DisplayName = "原owner草稿",
            };
            var profilePage = new UserProfileWebFactory(settings, originalViewModel,
                Path.Combine(Path.GetTempPath(), "fgopet-settings-deferred-profile-" + Guid.NewGuid().ToString("N")));
            var factory = new SettingsWebRootFactory(Catalog(Page("UserProfile", order: 1)), [profilePage],
                Path.Combine(Path.GetTempPath(), "fgopet-settings-deferred-root-" + Guid.NewGuid().ToString("N")));

            // Simulate a transient settings-store read failure after the legacy owner was
            // successfully constructed. Creating either the root session or view must not read it.
            settings.ThrowOnLoad = true;
            using var session = factory.CreateSession();
            using var host = factory.CreateView();

            var firstRead = await Send(session, "getProfile", "{\"pageId\":\"UserProfile\"}");
            Assert.False(firstRead.Success);
            Assert.Equal("SETTINGS_UNAVAILABLE", firstRead.ErrorCode);

            settings.ThrowOnLoad = false;
            var retry = await Send(session, "getProfile", "{\"pageId\":\"UserProfile\"}");
            Assert.True(retry.Success);
            using var retryPayload = JsonDocument.Parse(JsonSerializer.Serialize(retry.Payload));
            Assert.Equal("原名称", retryPayload.RootElement.GetProperty("displayName").GetString());

            var save = await Send(session, "saveProfile",
                "{\"pageId\":\"UserProfile\",\"displayName\":\"root保存\"}");
            Assert.True(save.Success);
            Assert.Equal("原owner草稿", originalViewModel.DisplayName);
            Assert.Equal("root保存", settings.Load().UserProfile?.DisplayName);
            Assert.Equal(0.72, settings.Load().Scale);
        });
    }

    private static SettingsWebRootFactory Factory(SettingsPageCatalog catalog, ModuleRoot modules,
        params ISettingsWebPage[] pages) => new(catalog, pages, modules.Path);

    private static SettingsPageCatalog Catalog(params SettingsPageViewFactory[] factories) => new(factories);

    private static SettingsPageViewFactory Page(string id, int order, string? title = null) =>
        new(id, _ => throw new InvalidOperationException(), title ?? id, group: "测试", order: order);

    private static async Task<string?> SelectedPage(SettingsWebRootFactory.SettingsWebRootSession session)
    {
        var result = await Send(session, "settings.getCatalog", "{}");
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result.Payload));
        return json.RootElement.GetProperty("selectedPageId").GetString();
    }

    private static async Task<string> Script(WebView2 web, string code) =>
        JsonSerializer.Deserialize<string>(await web.CoreWebView2.ExecuteScriptAsync(code)) ?? string.Empty;

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(25);
        Assert.True(condition());
    }

    private static async Task WaitUntil(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!await condition() && DateTime.UtcNow < deadline) await Task.Delay(25);
        Assert.True(await condition());
    }

    private static Task<WebSurfaceCommandResult> Send(SettingsWebRootFactory.SettingsWebRootSession session,
        string command, string payload) => session.HandleCommandAsync(
        new WebSurfaceMessage(command, "request-1", JsonDocument.Parse(payload).RootElement.Clone()),
        CancellationToken.None).AsTask();

    private sealed class ModuleRoot : IDisposable
    {
        public ModuleRoot() => Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fgopet-settings-modules-" + Guid.NewGuid().ToString("N"));
        public string Path { get; }
        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }

    private sealed class FakePage(string pageId, string modulePath, IEnumerable<string> commands) : ISettingsWebPage
    {
        private readonly Func<WebSurfaceMessage, CancellationToken, ValueTask<WebSurfaceCommandResult>> _handler =
            (_, _) => ValueTask.FromResult(new WebSurfaceCommandResult(true));

        public string SettingsPageId { get; set; } = pageId;
        public string ModulePath { get; set; } = modulePath;
        public List<string> CommandsMutable { get; } = [.. commands];
        public IReadOnlyList<string> Commands => CommandsMutable;
        public int CallCount { get; private set; }

        public ValueTask<WebSurfaceCommandResult> HandleCommandAsync(WebSurfaceMessage message,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return _handler(message, cancellationToken);
        }
    }

    private sealed class PausedOwner(string pageId, string modulePath, string command) : ISettingsWebPage
    {
        public string SettingsPageId { get; } = pageId;
        public string ModulePath { get; } = modulePath;
        public IReadOnlyList<string> Commands { get; } = [command];
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken? ObservedToken { get; private set; }
        public int CommitCount { get; private set; }

        public async ValueTask<WebSurfaceCommandResult> HandleCommandAsync(WebSurfaceMessage message,
            CancellationToken cancellationToken)
        {
            ObservedToken = cancellationToken;
            Entered.TrySetResult();
            await Continue.Task;
            cancellationToken.ThrowIfCancellationRequested();
            CommitCount++;
            return new(true);
        }
    }

    private sealed class Store(CharacterSettings initial) : ICharacterSettingsStore
    {
        private CharacterSettings _settings = initial;
        public bool ThrowOnLoad { get; set; }
        public CharacterSettings Load() => ThrowOnLoad
            ? throw new IOException("Temporary settings-store read failure.")
            : _settings;
        public void Save(CharacterSettings settings) => _settings = settings;
    }
}
