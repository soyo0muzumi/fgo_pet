using System.Text.Json;
using FgoPet.App.Dialogue;
using FgoPet.Extensibility;
using FgoPet.Kernel.Agent;
using FgoPet.Plugin.Browser;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

public sealed class BrowserApprovalIntegrationTests
{
    [Theory]
    [InlineData("browser.click", "allow")]
    [InlineData("browser.fill", "allow")]
    [InlineData("browser.click", "revoke")]
    [InlineData("browser.fill", "arguments")]
    [InlineData("browser.click", "reconnect")]
    [InlineData("browser.click", "pre-dispatch-revoke")]
    public async Task Actual_browser_owner_requires_frozen_approval_and_never_replays_a_consumed_snapshot(string name, string mode)
    {
        var scope = new ToolScope("conversation", "role", null);
        // Existing assembly paths satisfy read-only readiness; Wire replaces all external process/browser access.
        var settings = new ChromeBrowserSettings(true, typeof(ChromeBrowserPlugin).Assembly.Location, typeof(ChromeBrowserPlugin).Assembly.Location);
        var wire = new Wire();
        var plugin = new ChromeBrowserPlugin(() => settings, s => s == scope, wire);
        var catalog = PluginCatalog.Create([plugin]);
        await using var runtime = new PluginRuntime(catalog);
        Assert.True((await runtime.StartAsync(default)).Succeeded);
        var registry = new ToolRegistry(catalog, runtime);
        var broker = new ApprovalBroker();
        var pipeline = new ToolExecutionPipeline(registry, new(registry, scope, new Fence()), new NativeSceneToolPolicy(), broker);
        var intent = new Intent();
        var read = await plugin.Contributions.Tools.Single(t => t.Descriptor.Name == "browser.read")
            .InvokeAsync(new(scope, JsonSerializer.SerializeToElement(new { pageId = 7 })), default);
        Assert.True(read.Success);
        var args = new Dictionary<string, object>
        {
            ["pageId"] = 7, ["snapshotId"] = read.Payload.GetProperty("snapshotId").GetString()!,
            ["uid"] = name == "browser.fill" ? "1_3" : "1_2"
        };
        if (name == "browser.fill") args["value"] = "合成输入😀";
        var request = new ToolExecutionRequest(new("run", "user", scope, "model", 1), 1,
            new("call", name, JsonSerializer.Serialize(args)));
        var waiting = await pipeline.AdvanceAsync(request, intent, default);
        Assert.Equal(ToolExecutionOutcomeKind.WaitingApproval, waiting.Kind);
        Assert.Equal(0, wire.Actions); Assert.Equal(0, intent.Commits);
        var approval = waiting.ApprovalRequest!;
        Assert.Contains("https://example.org/", approval.Tool.BusinessConfirmation!.PlainTextSummary);
        Assert.Equal(ApprovalDecision.Allow, broker.Validate(approval,
            new("run", approval.Binding.RequestId, approval.Binding.WaitingRevision, ApprovalDecision.Allow), scope));
        if (mode == "revoke") settings = settings with { AuthorizationId = Guid.NewGuid().ToString("N") };
        if (mode == "arguments")
        {
            args["value"] = "different";
            request = request with { Call = new("call", name, JsonSerializer.Serialize(args)) };
        }
        if (mode == "reconnect") Assert.True((await plugin.ReconnectAsync()).IsAvailable);
        if (mode == "pre-dispatch-revoke") intent.BeforeCommit = () => settings = settings with { Enabled = false };
        var approved = request with { Approval = approval };
        var result = await pipeline.AdvanceAsync(approved, intent, default);
        Assert.Equal(mode == "allow", result.Result!.Success);
        Assert.Equal(mode == "allow" ? 1 : 0, wire.Actions);
        if (mode == "allow")
        {
            Assert.Equal(ToolExecutionState.Committed, result.Result.ExecutionState);
            Assert.False((await pipeline.AdvanceAsync(approved, intent, default)).Result!.Success);
            Assert.Equal(1, wire.Actions);
        }
    }

    private sealed class Fence : IAgentRunFence
    { public void EnsureCurrent(AgentRunIdentity identity, CancellationToken token) => token.ThrowIfCancellationRequested(); }
    private sealed class Intent : IToolExecutionIntent
    {
        public int Commits;
        public Action? BeforeCommit;
        public ValueTask CommitStartedAsync(ToolDescriptor descriptor, CancellationToken token)
        { Commits++; BeforeCommit?.Invoke(); return ValueTask.CompletedTask; }
    }
    private sealed class Wire : IChromeMcpConnection
    {
        public int Actions;
        public ValueTask<JsonElement> CallAsync(ChromeBrowserSettings settings, string name, JsonElement args, CancellationToken token)
        {
            if (name is "click" or "fill")
            {
                Actions++;
                Assert.Equal(7, args.GetProperty("pageId").GetInt32());
                if (name == "fill") Assert.Equal("合成输入😀", args.GetProperty("value").GetString());
                return ValueTask.FromResult(JsonSerializer.SerializeToElement(new { content = new[] { new { type = "text", text = "Acknowledged" } } }));
            }
            if (name == "take_snapshot") return ValueTask.FromResult(JsonSerializer.SerializeToElement(new
            {
                content = new[] { new { type = "text", text = "uid=1_2 button \"Expand\"\nuid=1_3 textbox \"Search\"" } },
                structuredContent = new { snapshot = new { url = "https://example.org/" } }
            }));
            return ValueTask.FromResult(JsonSerializer.SerializeToElement(new { structuredContent = new { pages = new[]
                { new { id = 7, url = "https://example.org/", title = "Fixture", selected = true } } } }));
        }
        public ValueTask ResetAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
