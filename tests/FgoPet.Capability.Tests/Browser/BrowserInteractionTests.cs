using System.Text.Json;
using FgoPet.Extensibility;
using FgoPet.Plugin.Browser;
using Xunit;

namespace FgoPet.Capability.Tests.Browser;

public sealed class BrowserInteractionTests
{
    private static readonly ToolScope Scope = new("conversation", "role", null);
    private static readonly ChromeBrowserSettings Enabled = new(true, "node.exe", "server.js");

    [Theory]
    [InlineData("browser.click", "click")]
    [InlineData("browser.fill", "fill")]
    public async Task Frozen_owner_confirmation_executes_once_and_requires_a_fresh_snapshot(string name, string action)
    {
        var connection = new ChromeBrowserTests.Connection { Text = "uid=1_2 textbox Synthetic" };
        await using var plugin = new ChromeBrowserPlugin(() => Enabled, scope => scope == Scope, connection);
        await plugin.StartAsync(default);
        var invocation = await Prepare(plugin, name);
        var tool = plugin.Contributions.Tools.Single(tool => tool.Descriptor.Name == name);

        var result = await tool.InvokeAsync(invocation, default);

        Assert.True(result.Success);
        Assert.Equal(ToolExecutionState.Committed, result.ExecutionState);
        var call = Assert.Single(connection.Calls.Where(call => call.Name == action));
        Assert.Equal("1_2", call.Args.GetProperty("uid").GetString());
        if (action == "fill") Assert.Equal("Synthetic replacement", call.Args.GetProperty("value").GetString());
        var replay = await tool.InvokeAsync(invocation, default);
        Assert.False(replay.Success);
        Assert.Equal(ToolExecutionState.NotExecuted, replay.ExecutionState);
        Assert.Single(connection.Calls.Where(call => call.Name == action));
    }

    [Theory]
    [InlineData("browser.click", "snapshot")]
    [InlineData("browser.fill", "snapshot")]
    [InlineData("browser.click", "grant")]
    [InlineData("browser.fill", "grant")]
    [InlineData("browser.click", "arguments")]
    [InlineData("browser.fill", "arguments")]
    public async Task Changed_snapshot_grant_or_arguments_never_dispatch_a_frozen_operation(string name, string change)
    {
        var connection = new ChromeBrowserTests.Connection { Text = "uid=1_2 textbox Synthetic\nuid=1_3 button Other" };
        var settings = Enabled;
        await using var plugin = new ChromeBrowserPlugin(() => settings, scope => scope == Scope, connection);
        await plugin.StartAsync(default);
        var invocation = await Prepare(plugin, name);
        if (change == "snapshot") Assert.True((await ChromeBrowserTests.Invoke(plugin, "browser.read", new { pageId = 7 })).Success);
        if (change == "grant") settings = settings with { Enabled = false };
        if (change == "arguments") invocation = invocation with
        {
            Arguments = JsonSerializer.SerializeToElement(name == "browser.fill"
                ? (object)new { pageId = 7, snapshotId = invocation.Arguments.GetProperty("snapshotId").GetString(), uid = "1_2", value = "Changed" }
                : new { pageId = 7, snapshotId = invocation.Arguments.GetProperty("snapshotId").GetString(), uid = "1_3" })
        };
        var result = await plugin.Contributions.Tools.Single(tool => tool.Descriptor.Name == name).InvokeAsync(invocation, default);
        Assert.False(result.Success);
        Assert.Equal(ToolExecutionState.NotExecuted, result.ExecutionState);
        Assert.DoesNotContain(connection.Calls, call => call.Name is "click" or "fill");
    }

    private static async Task<ToolInvocation> Prepare(ChromeBrowserPlugin plugin, string name)
    {
        var read = await ChromeBrowserTests.Invoke(plugin, "browser.read", new { pageId = 7 });
        Assert.True(read.Success);
        var snapshotId = read.Payload.GetProperty("snapshotId").GetString();
        var invocation = new ToolInvocation(Scope, JsonSerializer.SerializeToElement(name == "browser.fill"
            ? (object)new { pageId = 7, snapshotId, uid = "1_2", value = "Synthetic replacement" }
            : new { pageId = 7, snapshotId, uid = "1_2" }));
        var tool = plugin.Contributions.Tools.Single(tool => tool.Descriptor.Name == name);
        var confirmation = ((IToolBusinessConfirmationProvider)tool).PrepareConfirmation(invocation, default);
        Assert.Contains("Synthetic", confirmation.PlainTextSummary);
        return invocation with { ExecutionContext = new("synthetic-run", 1, "synthetic-call", new string('A', 64))
        {
            ResourceAuthorization = ((IToolResourceAuthorizationProvider)tool).GetAuthorization(Scope),
            BusinessConfirmation = confirmation,
        } };
    }
}
