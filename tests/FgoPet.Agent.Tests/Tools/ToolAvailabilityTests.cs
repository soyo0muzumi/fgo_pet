using FgoPet.Extensibility;
using FgoPet.Kernel.Agent;
using Xunit;

namespace FgoPet.Agent.Tests.Tools;

public sealed class ToolAvailabilityTests
{
    [Fact]
    public async Task Model_catalog_explains_current_setup_failure_and_recovers_without_mutating_catalog()
    {
        var plugin = new Fixture();
        var catalog = PluginCatalog.Create([plugin]);
        await using var runtime = new PluginRuntime(catalog);
        var tools = new ToolRegistry(catalog, runtime);
        var scope = new ToolScope("conversation", "role", null);
        Assert.Empty(tools.GetTools(scope));
        Assert.True((await runtime.StartAsync(default)).Succeeded);

        var unavailable = Assert.Single(tools.GetTools(scope));
        Assert.Contains("BROWSER_NOT_CONFIGURED", unavailable.Description);
        Assert.Contains("Do not call or retry", unavailable.Description);
        Assert.Equal("fixture.read", unavailable.Name);
        Assert.Equal(plugin.Descriptor.Parameters.GetRawText(), unavailable.Parameters.GetRawText());
        Assert.Equal("Read a fixture.", catalog.Tools[0].Descriptor.Description);
        Assert.Equal(0, plugin.Invocations);

        plugin.Availability = new(true);
        Assert.Same(plugin.Descriptor, Assert.Single(tools.GetTools(scope)));
        plugin.Availability = new(false, "BROWSER_NOT_ENABLED");
        Assert.Contains("BROWSER_NOT_ENABLED", Assert.Single(tools.GetTools(scope)).Description);
        runtime.CloseAdmission();
        Assert.Empty(tools.GetTools(scope));
    }

    [Theory]
    [InlineData(false, "private-path-with-secret")]
    [InlineData(true, "private-path-with-secret")]
    public async Task Broken_status_provider_cannot_leak_arbitrary_text_or_break_other_catalog_reads(bool throws, string sensitive)
    {
        var plugin = new Fixture { Throws = throws, Availability = new(false, sensitive) };
        var catalog = PluginCatalog.Create([plugin]);
        await using var runtime = new PluginRuntime(catalog);
        await runtime.StartAsync(default);
        var descriptor = Assert.Single(new ToolRegistry(catalog, runtime).GetTools(new("conversation", "role", null)));
        Assert.DoesNotContain(sensitive, descriptor.Description);
        Assert.Contains(throws ? "TOOL_STATUS_UNAVAILABLE" : "TOOL_NOT_READY", descriptor.Description);
    }

    private sealed class Fixture : IFgoPetPlugin, IToolProvider, IToolAvailabilityProvider
    {
        public PluginManifest Manifest { get; } = new("fixture", "1.0.0", 1, []);
        public PluginContributions Contributions => PluginContributions.Empty with { Tools = [this] };
        public ToolDescriptor Descriptor { get; } = new("fixture.read", "Read a fixture.", "{\"type\":\"object\"}", ToolEffect.ReadOnly);
        public ToolAvailability Availability { get; set; } = new(false, "BROWSER_NOT_CONFIGURED");
        public bool Throws { get; init; }
        public int Invocations { get; private set; }
        public ToolAvailability GetAvailability() => Throws ? throw new IOException("private-path-with-secret") : Availability;
        public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken token)
        { Invocations++; return ValueTask.FromResult(new ToolResult(true, invocation.Arguments)); }
        public ValueTask StartAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask StopAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
