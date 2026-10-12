using System.Text.Json;
using FgoPet.Extensibility;
using FgoPet.Plugin.DesktopContext;
using Xunit;

namespace FgoPet.Capability.Tests.DesktopContext;

public sealed class DesktopContextTests
{
    private static readonly ToolScope Scope = new("conversation", "role", null);

    [Fact]
    public async Task Clock_samples_real_provider_once_and_preserves_zone_and_offset()
    {
        var clock = new Clock();
        await using var plugin = new DesktopContextPlugin(s => s == Scope, clock);
        await plugin.StartAsync(default);
        var tool = Assert.Single(plugin.Contributions.Tools);
        Assert.Equal("context.time", tool.Descriptor.Name);
        var result = await Invoke(tool);
        Assert.True(result.Success);
        Assert.Equal(clock.Now, result.Payload.GetProperty("sampledAtUtc").GetDateTimeOffset());
        var local = result.Payload.GetProperty("localTime").GetDateTimeOffset();
        Assert.Equal(clock.Now, local.ToUniversalTime());
        Assert.Equal(TimeSpan.FromHours(5.5), local.Offset);
        Assert.Equal("FixtureZone", result.Payload.GetProperty("timeZoneId").GetString());
        Assert.Equal(1, clock.Samples);
        Assert.Equal("TOOL_INVALID_ARGUMENTS", (await Invoke(tool, new { command = "anything" })).ErrorCode);
        await plugin.StopAsync(default);
        Assert.Equal("CONTEXT_STOPPED", (await Invoke(tool)).ErrorCode);
    }

    [Fact]
    public async Task Application_discovery_is_absent_without_both_host_grant_and_reader()
    {
        var called = false;
        await using var plugin = new DesktopContextPlugin(_ => true, readApplications: _ => { called = true; throw new InvalidOperationException(); });
        await plugin.StartAsync(default);
        Assert.DoesNotContain(plugin.Contributions.Tools, t => t.Descriptor.Name == "context.apps");
        Assert.False(called);
    }

    [Fact]
    public async Task Revocation_discards_application_observation_and_never_exposes_titles_or_paths()
    {
        var allowed = true;
        var clock = new Clock();
        var snapshot = new ApplicationSnapshot([new("fixture", 2, true)], false, true, clock.Now, clock.Now);
        await using var plugin = new DesktopContextPlugin(_ => true, clock, _ => allowed, _ => { allowed = false; return snapshot; });
        await plugin.StartAsync(default);
        var tool = plugin.Contributions.Tools.Single(t => t.Descriptor.Name == "context.apps");
        var result = await Invoke(tool);
        Assert.Equal("CONTEXT_AUTHORIZATION_CHANGED", result.ErrorCode);
        Assert.DoesNotContain("fixture", result.Payload.GetRawText());
    }

    [Fact]
    public async Task Applications_are_bounded_observations_not_process_inventory_or_content()
    {
        var clock = new Clock();
        var snapshot = new ApplicationSnapshot(Enumerable.Range(0, 40).Select(i => new OpenApplication("fixture" + i, 1, i == 0)).ToArray(),
            false, true, clock.Now, clock.Now);
        await using var plugin = new DesktopContextPlugin(_ => true, clock, _ => true, _ => snapshot);
        await plugin.StartAsync(default);
        var result = await Invoke(plugin.Contributions.Tools.Single(t => t.Descriptor.Name == "context.apps"));
        Assert.True(result.Success);
        Assert.Equal(32, result.Payload.GetProperty("applications").GetArrayLength());
        Assert.True(result.Payload.GetProperty("truncated").GetBoolean());
        Assert.Equal("not_read", result.Payload.GetProperty("contentAccess").GetString());
        Assert.DoesNotContain("title", result.Payload.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("path", result.Payload.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Stale_scope_and_cancellation_prevent_sampling()
    {
        var clock = new Clock();
        await using var plugin = new DesktopContextPlugin(_ => false, clock);
        await plugin.StartAsync(default);
        var tool = Assert.Single(plugin.Contributions.Tools);
        Assert.Equal("CONTEXT_SCOPE_DENIED", (await Invoke(tool)).ErrorCode);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tool.InvokeAsync(new(Scope, JsonSerializer.SerializeToElement(new { })), cancellation.Token).AsTask());
        Assert.Equal(0, clock.Samples);
    }

    private static Task<ToolResult> Invoke(IToolProvider tool, object? args = null) =>
        tool.InvokeAsync(new(Scope, JsonSerializer.SerializeToElement(args ?? new { })), default).AsTask();
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; } = new(2026, 10, 11, 21, 32, 43, TimeSpan.Zero);
        public int Samples { get; private set; }
        public override DateTimeOffset GetUtcNow() { Samples++; return Now; }
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.CreateCustomTimeZone("FixtureZone", TimeSpan.FromHours(5.5), "fixture", "fixture");
    }
}
