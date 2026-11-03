using System.Text.Json;
using FgoPet.Extensibility;
using FgoPet.Plugin.Browser;
using Xunit;

namespace FgoPet.Capability.Tests.Browser;

public sealed class BrowserReadinessTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "browser-check-" + Guid.NewGuid().ToString("N"));
    private ChromeBrowserSettings Settings => new(true, Path.Combine(_directory, "node.exe"), Path.Combine(_directory, "server.js"));

    public BrowserReadinessTests()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Settings.NodePath, "synthetic runtime");
        File.WriteAllText(Settings.ServerPath, "synthetic server");
    }

    [Theory]
    [InlineData("BROWSER_NOT_CONFIGURED")]
    [InlineData("BROWSER_SETTINGS_INVALID")]
    [InlineData("BROWSER_SETTINGS_UNREADABLE")]
    [InlineData("BROWSER_DEPENDENCY_MISSING")]
    [InlineData("BROWSER_NOT_ENABLED")]
    public async Task Configuration_failure_is_identical_for_tool_availability_invocation_and_connection_check(string error)
    {
        var wire = new CheckWire();
        await using var plugin = new ChromeBrowserPlugin(() => Settings, _ => true, wire, configurationError: () => error);
        await plugin.StartAsync(default);
        var tool = plugin.Contributions.Tools.First();
        Assert.Equal(error, ((IToolAvailabilityProvider)tool).GetAvailability().ErrorCode);
        Assert.Equal(error, (await ChromeBrowserTests.Invoke(plugin, "browser.tabs", new { })).ErrorCode);
        Assert.Equal(error, (await plugin.CheckConnectionAsync()).ErrorCode);
        Assert.Equal(error, (await plugin.ReconnectAsync()).ErrorCode);
        Assert.Equal(0, wire.Calls);
    }

    [Fact]
    public async Task Successful_check_returns_status_only_and_does_not_grant_visual_permission()
    {
        var wire = new CheckWire();
        var diagnostics = new List<BrowserDiagnostic>();
        await using var plugin = new ChromeBrowserPlugin(() => Settings, _ => true, wire, diagnostics.Add);
        await plugin.StartAsync(default);
        Assert.True(plugin.GetAvailability().IsAvailable);
        Assert.Equal(0, wire.Calls);
        var result = await plugin.CheckConnectionAsync();
        Assert.True(result.IsAvailable);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(result));
        Assert.DoesNotContain("private", JsonSerializer.Serialize(diagnostics));
        Assert.Equal(1, wire.Calls);
        Assert.Equal("list_pages", wire.LastMethod);
        Assert.Equal("BROWSER_VISUAL_NOT_ENABLED", ((IToolAvailabilityProvider)plugin.Contributions.Tools.Last()).GetAvailability().ErrorCode);
    }

    [Fact]
    public async Task Disabled_check_and_reconnect_never_contact_the_browser()
    {
        var wire = new CheckWire();
        await using var plugin = new ChromeBrowserPlugin(() => Settings with { Enabled = false }, _ => true, wire);
        await plugin.StartAsync(default);
        Assert.Equal("BROWSER_NOT_ENABLED", (await plugin.CheckConnectionAsync()).ErrorCode);
        Assert.Equal("BROWSER_NOT_ENABLED", (await plugin.ReconnectAsync()).ErrorCode);
        Assert.Equal(0, wire.Calls);
    }

    [Fact]
    public async Task Reconnect_resets_owned_connection_before_checking()
    {
        var wire = new CheckWire();
        await using var plugin = new ChromeBrowserPlugin(() => Settings, _ => true, wire);
        await plugin.StartAsync(default);
        Assert.True((await plugin.CheckConnectionAsync()).IsAvailable);
        Assert.True((await plugin.ReconnectAsync()).IsAvailable);
        Assert.Equal(new[] { "call", "reset", "call" }, wire.Events);
    }

    [Fact]
    public async Task Reconnect_invalidates_snapshot_cursors()
    {
        var wire = new ChromeBrowserTests.Connection();
        await using var plugin = new ChromeBrowserPlugin(() => Settings, _ => true, wire);
        await plugin.StartAsync(default);
        var first = await ChromeBrowserTests.Invoke(plugin, "browser.read", new { pageId = 7, limit = 3 });
        var cursor = first.Payload.GetProperty("nextCursor").GetString();
        Assert.NotNull(cursor);
        Assert.True((await plugin.ReconnectAsync()).IsAvailable);
        Assert.Equal("BROWSER_CURSOR_EXPIRED", (await ChromeBrowserTests.Invoke(plugin, "browser.read", new { pageId = 7, cursor })).ErrorCode);
    }

    [Fact]
    public async Task Revocation_while_check_is_running_discards_success_and_resets_connection()
    {
        var settings = Settings;
        var wire = new CheckWire { DuringCall = () => settings = settings with { Enabled = false } };
        await using var plugin = new ChromeBrowserPlugin(() => settings, _ => true, wire);
        await plugin.StartAsync(default);
        Assert.Equal("BROWSER_AUTHORIZATION_CHANGED", (await plugin.CheckConnectionAsync()).ErrorCode);
        Assert.Contains("reset", wire.Events);
    }

    [Fact]
    public async Task Connection_error_is_preserved_without_returning_exception_text()
    {
        var wire = new CheckWire { ErrorCode = "BROWSER_CONNECTION_CLOSED" };
        await using var plugin = new ChromeBrowserPlugin(() => Settings, _ => true, wire);
        await plugin.StartAsync(default);
        Assert.Equal("BROWSER_CONNECTION_CLOSED", (await plugin.CheckConnectionAsync()).ErrorCode);
        Assert.Contains("reset", wire.Events);
    }

    [Fact]
    public async Task Cancelling_connection_check_releases_gate_and_allows_the_next_check()
    {
        var wire = new CheckWire { Wait = true };
        await using var plugin = new ChromeBrowserPlugin(() => Settings, _ => true, wire);
        await plugin.StartAsync(default);
        using var cancel = new CancellationTokenSource();
        var checking = plugin.CheckConnectionAsync(cancel.Token).AsTask();
        await wire.Entered.Task;
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => checking);
        wire.Wait = false;
        Assert.True((await plugin.CheckConnectionAsync()).IsAvailable);
        Assert.Contains("reset", wire.Events);
    }

    [Fact]
    public async Task Shutdown_cancels_running_and_queued_connection_checks_without_a_second_dispatch()
    {
        var wire = new CheckWire { Wait = true };
        await using var plugin = new ChromeBrowserPlugin(() => Settings, _ => true, wire);
        await plugin.StartAsync(default);
        var first = plugin.CheckConnectionAsync().AsTask();
        await wire.Entered.Task;
        var queued = plugin.CheckConnectionAsync().AsTask();
        var stopped = plugin.StopAsync(default).AsTask();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        await stopped;
        Assert.Equal(1, wire.Calls);
        Assert.Equal("BROWSER_STOPPED", plugin.GetAvailability().ErrorCode);
    }

    [Fact]
    public async Task Readiness_reports_removed_runtime_without_starting_a_process()
    {
        var wire = new CheckWire();
        await using var plugin = new ChromeBrowserPlugin(() => Settings, _ => true, wire);
        await plugin.StartAsync(default);
        File.Delete(Settings.NodePath);
        Assert.Equal("BROWSER_DEPENDENCY_MISSING", plugin.GetAvailability().ErrorCode);
        Assert.Equal("BROWSER_DEPENDENCY_MISSING", (await plugin.CheckConnectionAsync()).ErrorCode);
        Assert.Equal(0, wire.Calls);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private sealed class CheckWire : IChromeMcpConnection
    {
        public int Calls { get; private set; }
        public string? LastMethod { get; private set; }
        public Action? DuringCall { get; init; }
        public string? ErrorCode { get; init; }
        public bool Wait { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Events { get; } = [];
        public async ValueTask<JsonElement> CallAsync(ChromeBrowserSettings settings, string name, JsonElement arguments, CancellationToken token)
        {
            Calls++;
            LastMethod = name;
            Events.Add("call");
            Entered.TrySetResult();
            if (Wait) await Task.Delay(Timeout.InfiniteTimeSpan, token);
            DuringCall?.Invoke();
            if (ErrorCode is not null) throw new BrowserException(ErrorCode);
            return JsonSerializer.SerializeToElement(new { structuredContent = new { pages = new[] { new { id = 1, url = "https://private.example/", title = "private page" } } } });
        }
        public ValueTask ResetAsync() { Events.Add("reset"); return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() => ResetAsync();
    }
}
