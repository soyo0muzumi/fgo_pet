using System.IO;
using System.Text.Json;
using FgoPet.App.Settings;
using FgoPet.Extensibility;
using FgoPet.Plugin.Browser;
using FgoPet.Plugin.WebSearch;
using FgoPet.UiSdk;
using Xunit;

namespace FgoPet.App.Tests.Settings;

public sealed class WebAccessSettingsWebPageTests
{
    [Fact]
    public async Task Opening_settings_neither_creates_grants_nor_tests_connections()
    {
        using var fixture = new Fixture();
        var result = await fixture.Send("webAccess.get", "{}");
        var snapshot = Assert.IsType<WebAccessSnapshot>(result.Payload);
        Assert.False(snapshot.Search.Configured);
        Assert.False(snapshot.Browser.Configured);
        Assert.Equal("WEB_SEARCH_NOT_CONFIGURED", snapshot.Search.ErrorCode);
        Assert.Equal("BROWSER_NOT_CONFIGURED", snapshot.Browser.ErrorCode);
        Assert.Equal(0, fixture.Checks);
        Assert.False(Directory.Exists(fixture.Root));
    }

    [Theory]
    [InlineData("webAccess.search.setEnabled", "{\"enabled\":\"true\"}")]
    [InlineData("webAccess.search.setEnabled", "{\"enabled\":true,\"enabled\":false}")]
    [InlineData("webAccess.get", "{\"enabled\":true}")]
    [InlineData("webAccess.browser.save", "{\"nodePath\":\"x\",\"serverPath\":\"y\",\"VisualEnabled\":true}")]
    [InlineData("webAccess.browser.useBundled", "{\"pageId\":\"WebAccess\"}")]
    [InlineData("webAccess.browser.check", "{\"url\":\"https://fixture.invalid\"}")]
    public async Task Invalid_or_forged_commands_do_not_write_or_connect(string command, string json)
    {
        using var fixture = new Fixture();
        var result = await fixture.Send(command, json);
        Assert.False(result.Success);
        Assert.Equal("SETTINGS_INVALID_INPUT", result.ErrorCode);
        Assert.Equal(0, fixture.Checks);
        Assert.False(Directory.Exists(fixture.Root));
    }

    [Fact]
    public async Task Enabling_a_removed_browser_setup_does_not_report_a_successful_save()
    {
        using var fixture = new Fixture();
        var result = await fixture.Send("webAccess.browser.setEnabled", "{\"enabled\":true}");
        Assert.False(result.Success);
        Assert.Equal("BROWSER_NOT_CONFIGURED", result.ErrorCode);
        Assert.False(Directory.Exists(fixture.Root));
    }

    [Fact]
    public async Task Explicit_bundled_setup_grants_browser_images_and_uses_application_paths()
    {
        using var fixture = new Fixture();
        var result = await fixture.Send("webAccess.browser.useBundled", "{}");
        var snapshot = Assert.IsType<WebAccessSnapshot>(result.Payload);
        Assert.True(snapshot.Browser.Enabled);
        Assert.True(snapshot.Browser.VisualEnabled);
        Assert.False(snapshot.Browser.Ready);
        Assert.Equal("BROWSER_DEPENDENCY_MISSING", snapshot.Browser.ErrorCode);
        Assert.Equal(Path.Combine(fixture.Root, "app", "Browser", "node.exe"), snapshot.Browser.NodePath);
        Assert.Equal(0, fixture.Checks);
    }

    [Fact]
    public async Task Cancelled_route_does_not_write_settings()
    {
        using var fixture = new Fixture();
        using var route = new CancellationTokenSource();
        route.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Send(
            "webAccess.search.setEnabled", "{\"enabled\":true}", route.Token));
        Assert.False(Directory.Exists(fixture.Root));
    }

    [Fact]
    public async Task Late_connection_result_cannot_report_success_after_revocation()
    {
        using var fixture = new Fixture();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource<ToolAvailability>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.CreateBrowserDependencies();
        await fixture.Send("webAccess.browser.useBundled", "{}");
        fixture.BrowserCheck = _ => { started.SetResult(); return new(completed.Task); };
        var pending = fixture.Send("webAccess.browser.check", "{}");
        await started.Task;
        await fixture.Browser.SetEnabledAsync(false);
        completed.SetResult(new(true));
        var snapshot = Assert.IsType<WebAccessSnapshot>((await pending).Payload);
        Assert.False(snapshot.Browser.Enabled);
        Assert.Null(snapshot.Browser.CheckSucceeded);
        Assert.Null(snapshot.Browser.CheckedAtUtc);
    }

    [Fact]
    public async Task Check_status_is_session_local_and_only_reported_after_a_real_check()
    {
        using var fixture = new Fixture();
        fixture.CreateBrowserDependencies();
        await fixture.Send("webAccess.browser.useBundled", "{}");
        var initial = Assert.IsType<WebAccessSnapshot>((await fixture.Send("webAccess.get", "{}")).Payload);
        Assert.True(initial.Browser.Ready);
        Assert.Null(initial.Browser.CheckSucceeded);
        var checkedState = Assert.IsType<WebAccessSnapshot>((await fixture.Send("webAccess.browser.check", "{}")).Payload);
        Assert.True(checkedState.Browser.CheckSucceeded);
        Assert.NotNull(checkedState.Browser.CheckedAtUtc);
        var newSession = fixture.Page.CreateSession();
        var reread = await newSession.HandleCommandAsync(new("webAccess.get", "fixture",
            JsonSerializer.SerializeToElement(new { pageId = "WebAccess" })), default);
        Assert.Null(Assert.IsType<WebAccessSnapshot>(reread.Payload).Browser.CheckSucceeded);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "fgo-web-settings-" + Guid.NewGuid().ToString("N"));
        public ChromeBrowserSettingsStore Browser { get; }
        public WebAccessSettingsWebPage Page { get; }
        public int Checks { get; private set; }
        public Func<CancellationToken, ValueTask<ToolAvailability>>? BrowserCheck { get; set; }
        public Fixture()
        {
            Browser = new(Path.Combine(Root, "chrome-browser.json"));
            var search = new WebSearchSettingsStore(Path.Combine(Root, "web-search.json"), Path.Combine(Root, "worker.exe"));
            Page = new(search, Browser, Check, token => BrowserCheck?.Invoke(token) ?? Check(token), Check, Path.Combine(Root, "app"));
        }
        private ValueTask<ToolAvailability> Check(CancellationToken token)
        { token.ThrowIfCancellationRequested(); Checks++; return ValueTask.FromResult(new ToolAvailability(true)); }
        public Task<WebSurfaceCommandResult> Send(string command, string json, CancellationToken token = default)
        {
            var body = "{\"pageId\":\"WebAccess\"" + (json == "{}" ? "}" : "," + json[1..]);
            using var document = JsonDocument.Parse(body);
            return Page.HandleCommandAsync(new(command, "fixture", document.RootElement.Clone()), token).AsTask();
        }
        public void CreateBrowserDependencies()
        {
            var root = Path.Combine(Root, "app", "Browser");
            var server = Path.Combine(root, "node_modules", "chrome-devtools-mcp", "build", "src", "bin", "chrome-devtools-mcp.js");
            Directory.CreateDirectory(Path.GetDirectoryName(server)!);
            File.WriteAllText(Path.Combine(root, "node.exe"), "fixture");
            File.WriteAllText(server, "fixture");
        }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
    }
}
