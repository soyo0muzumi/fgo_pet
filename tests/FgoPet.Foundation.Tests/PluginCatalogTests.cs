using System.Collections.Immutable;
using System.Text.Json;
using FgoPet.Extensibility;
using Xunit;

namespace FgoPet.Foundation.Tests;

public sealed class PluginCatalogTests
{
    [Fact]
    public void Sample_plugin_contributes_tools_workspace_and_settings_without_core_types()
    {
        var sample = new SamplePlugin("test.todo");
        var catalog = PluginCatalog.Create([sample]);
        Assert.Equal("test.todo", Assert.Single(catalog.Plugins).Manifest.Id);
        Assert.Equal("echo", Assert.Single(catalog.Tools).Descriptor.Name);
        Assert.Equal("test.todo.workspace", Assert.Single(catalog.Workspaces).Descriptor.Id);
        Assert.Equal("test.todo.settings", Assert.Single(catalog.SettingsPages).Descriptor.Id);
        Assert.Empty(PluginCatalog.Create([]).Plugins);
    }

    [Fact]
    public void Captured_catalog_is_unchanged_when_a_plugin_replaces_its_contributions()
    {
        var sample = new SamplePlugin("test.todo");
        var catalog = PluginCatalog.Create([sample]);
        sample.Contributions = PluginContributions.Empty;
        Assert.Single(catalog.Tools);
        Assert.Single(catalog.Workspaces);
        Assert.Single(catalog.SettingsPages);
    }

    [Fact]
    public void Dependencies_are_ordered_before_dependents_regardless_of_registration_order()
    {
        var dependent = new SamplePlugin("test.dependent", ["test.base"], contributes: false);
        var basis = new SamplePlugin("test.base", contributes: false);
        Assert.Equal(new[] { "test.base", "test.dependent" }, PluginCatalog.Create([dependent, basis]).Plugins.Select(plugin => plugin.Manifest.Id));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("missing")]
    [InlineData("cycle")]
    [InlineData("tool")]
    public void Invalid_registration_is_rejected_with_a_safe_code(string failure)
    {
        IFgoPetPlugin[] plugins = failure switch
        {
            "duplicate" => [new SamplePlugin("test.one"), new SamplePlugin("test.one")],
            "missing" => [new SamplePlugin("test.one", ["test.missing"])],
            "cycle" => [new SamplePlugin("test.one", ["test.two"], false), new SamplePlugin("test.two", ["test.one"], false)],
            _ => [new SamplePlugin("test.one"), new SamplePlugin("test.two")]
        };
        var error = Assert.Throws<PluginValidationException>(() => PluginCatalog.Create(plugins));
        Assert.StartsWith("PLUGIN_", error.Code);
        Assert.DoesNotContain("secret", error.Message);
    }

    [Theory]
    [InlineData("Test.Invalid", "1.0.0", 1)]
    [InlineData("test.valid", "invalid", 1)]
    [InlineData("test.valid", "1.0.0", 2)]
    public void Unsupported_manifest_is_rejected(string id, string version, int apiVersion)
    {
        var sample = new SamplePlugin("test.valid", contributes: false)
        { Manifest = new(id, version, apiVersion, []) };
        Assert.Throws<PluginValidationException>(() => PluginCatalog.Create([sample]));
    }

    [Fact]
    public async Task Tool_handler_is_routed_through_its_captured_registration()
    {
        var catalog = PluginCatalog.Create([new SamplePlugin("test.todo")]);
        var tool = Assert.Single(catalog.Tools);
        var result = await tool.Provider.InvokeAsync(new(new("conversation", "role", null), JsonSerializer.SerializeToElement(new { text = "example" })), CancellationToken.None);
        Assert.True(result.Success);
        Assert.Equal("example", result.Payload.GetProperty("text").GetString());
    }

    internal sealed class SamplePlugin : IFgoPetPlugin
    {
        public PluginManifest Manifest { get; set; }
        public PluginContributions Contributions { get; set; }
        public SamplePlugin(string id, string[]? dependencies = null, bool contributes = true)
        {
            Manifest = new(id, "1.0.0", 1, (dependencies ?? []).ToImmutableArray());
            Contributions = contributes ? new([new EchoProvider()], [new(id + ".workspace", "Workspace")], [new(id + ".settings", "Settings")]) : PluginContributions.Empty;
        }
        public List<string> Events { get; } = [];
        public bool FailStart { get; set; }
        public bool FailStop { get; set; }
        public ValueTask StartAsync(CancellationToken stoppingToken)
        {
            Events.Add("start");
            if (FailStart) throw new InvalidOperationException("secret backend details");
            return ValueTask.CompletedTask;
        }
        public ValueTask StopAsync(CancellationToken cancellationToken)
        {
            Events.Add("stop");
            if (FailStop) throw new InvalidOperationException("secret backend details");
            return ValueTask.CompletedTask;
        }
        public ValueTask DisposeAsync() { Events.Add("dispose"); return ValueTask.CompletedTask; }
    }

    private sealed class EchoProvider : IToolProvider
    {
        public ToolDescriptor Descriptor { get; } = new("echo", "Test-only echo", "{\"type\":\"object\"}", ToolEffect.ReadOnly);
        public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken) => ValueTask.FromResult(new ToolResult(true, invocation.Arguments.Clone()));
    }
}
