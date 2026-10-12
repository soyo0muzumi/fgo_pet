using System.Text.Json;
using FgoPet.Plugin.Browser;
using Xunit;

namespace FgoPet.Capability.Tests.Browser;

public sealed class ChromeMcpConnectionTests
{
    [Fact]
    public async Task Explicit_portable_profile_is_passed_only_as_a_host_setting()
    {
        var defaults = Settings();
        var directory = Path.GetTempPath();
        var settings = JsonSerializer.Deserialize<ChromeBrowserSettings>(JsonSerializer.Serialize(new {
            defaults.Enabled, defaults.NodePath, defaults.ServerPath, UserDataDirectory = directory }))!;
        await using var connection = new ChromeMcpConnection();
        var result = await connection.CallAsync(settings, "list_pages", JsonSerializer.SerializeToElement(new { expectedDirectory = directory }), default);
        Assert.True(result.GetProperty("explicitProfile").GetBoolean());
    }

    private static ChromeBrowserSettings Settings()
    {
        var executable = OperatingSystem.IsWindows() ? "node.exe" : "node";
        var node = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(p => Path.Combine(p, executable)).FirstOrDefault(File.Exists);
        Assert.NotNull(node);
        return new(true, Path.GetFullPath(node), Path.Combine(AppContext.BaseDirectory, "Browser", "mcp-fixture.mjs"));
    }

    [Fact]
    public async Task Real_stdio_handshake_reuses_session_and_enforces_safe_launch_flags()
    {
        await using var connection = new ChromeMcpConnection();
        var first = await connection.CallAsync(Settings(), "list_pages", JsonSerializer.SerializeToElement(new { }), default);
        var second = await connection.CallAsync(Settings(), "take_snapshot", JsonSerializer.SerializeToElement(new { pageId = 7 }), default);
        Assert.Equal(1, first.GetProperty("sequence").GetInt32());
        Assert.Equal(2, second.GetProperty("sequence").GetInt32());
        Assert.True(first.GetProperty("safeFlags").GetBoolean());
    }

    [Theory]
    [InlineData("exit", "BROWSER_CONNECTION_CLOSED")]
    [InlineData("oversize", "BROWSER_PROTOCOL_LIMIT")]
    [InlineData("error", "BROWSER_PROTOCOL_FAILED")]
    public async Task Failed_call_is_not_retried_and_next_call_starts_fresh_session(string mode, string code)
    {
        await using var connection = new ChromeMcpConnection();
        var error = await Assert.ThrowsAsync<BrowserException>(() => connection.CallAsync(Settings(), "list_pages",
            JsonSerializer.SerializeToElement(new { fixtureMode = mode }), default).AsTask());
        Assert.Equal(code, error.Code);
        Assert.DoesNotContain("secret", error.ToString());
        var next = await connection.CallAsync(Settings(), "list_pages", JsonSerializer.SerializeToElement(new { }), default);
        Assert.Equal(1, next.GetProperty("sequence").GetInt32());
    }

    [Fact]
    public async Task Cancellation_closes_owned_server_and_does_not_poison_next_request()
    {
        await using var connection = new ChromeMcpConnection();
        // Finish startup before testing cancellation of a tool request.
        await connection.CallAsync(Settings(), "list_pages", JsonSerializer.SerializeToElement(new { }), default);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection.CallAsync(Settings(), "take_snapshot",
            JsonSerializer.SerializeToElement(new { fixtureMode = "wait" }), cancellation.Token).AsTask());
        var next = await connection.CallAsync(Settings(), "list_pages", JsonSerializer.SerializeToElement(new { }), default);
        Assert.Equal(1, next.GetProperty("sequence").GetInt32());
    }

    [Fact]
    public async Task Non_allowlisted_method_is_rejected_before_process_start()
    {
        await using var connection = new ChromeMcpConnection();
        var error = await Assert.ThrowsAsync<BrowserException>(() => connection.CallAsync(new(true, "missing", "missing"),
            "evaluate_script", JsonSerializer.SerializeToElement(new { }), default).AsTask());
        Assert.Equal("BROWSER_TOOL_DENIED", error.Code);
    }
}
