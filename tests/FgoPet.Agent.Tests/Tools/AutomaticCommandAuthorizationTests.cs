using System.Text.Json;
using FgoPet.Extensibility;
using FgoPet.Kernel.Agent;
using Xunit;

namespace FgoPet.Agent.Tests.Tools;

public sealed class AutomaticCommandAuthorizationTests
{
    [Theory]
    [InlineData("valid", true, 1)]
    [InlineData("absent", false, 0)]
    [InlineData("revoked", false, 0)]
    [InlineData("owner", false, 0)]
    [InlineData("tool", false, 0)]
    [InlineData("scope", false, 0)]
    [InlineData("after-intent", false, 1)]
    [InlineData("policy-revoked", false, 1)]
    public async Task Owner_grant_is_bound_and_revalidated_after_intent(string mode, bool success, int commits)
    {
        var scope = new ToolScope("chat", "role", null);
        var tool = new AutomaticTool(mode, scope);
        var catalog = PluginCatalog.Create([new Plugin(tool)]);
        using var runtime = new PluginRuntime(catalog);
        Assert.True((await runtime.StartAsync(default)).Succeeded);
        var registry = new ToolRegistry(catalog, runtime);
        var policy = new Policy();
        var pipeline = new ToolExecutionPipeline(registry, new ToolExecutor(registry, scope, new Fence()), policy);
        var intent = new Intent { BeforeCommit = () => { if (mode == "after-intent") tool.Current = false; if (mode == "policy-revoked") policy.Current = false; } };
        var result = await pipeline.AdvanceAsync(new(new("run", "user", scope, "model", 1), 1,
            new("call", tool.Descriptor.Name, "{}")), intent, default);
        Assert.Equal(success, result.Result!.Success);
        Assert.Equal(commits, intent.Commits);
        Assert.Equal(success ? 1 : 0, tool.Calls);
        if (!success) Assert.Equal("TOOL_AUTHORIZATION_DENIED", result.Result.ErrorCode);
    }

    private sealed class AutomaticTool(string mode, ToolScope scope) : IToolProvider, IToolAutomaticCommandAuthorizationProvider
    {
        public int Calls { get; private set; }
        public bool Current { get; set; } = mode != "revoked";
        public ToolDescriptor Descriptor { get; } = new("fixture.append", "Bounded synthetic write", "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}", ToolEffect.Command);
        public ToolAutomaticAuthorization? CaptureAutomaticAuthorization(ToolScope captured) => mode == "absent" ? null : new(
            mode == "owner" ? "other" : "fixture.auto", mode == "tool" ? "fixture.other" : Descriptor.Name,
            mode == "scope" ? scope with { ConversationId = "other" } : captured, () => Current);
        public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken token)
        {
            Assert.True(invocation.ExecutionContext!.AutomaticAuthorization!.IsCurrent());
            Assert.DoesNotContain(nameof(ToolExecutionContext.AutomaticAuthorization), JsonSerializer.Serialize(invocation.ExecutionContext));
            Calls++; return ValueTask.FromResult(new ToolResult(true, JsonSerializer.SerializeToElement(new { committed = true })) { ExecutionState = ToolExecutionState.Committed });
        }
    }
    private sealed class Plugin(IToolProvider provider) : IFgoPetPlugin
    {
        public PluginManifest Manifest { get; } = new("fixture.auto", "1.0.0", 1, []);
        public PluginContributions Contributions { get; } = new([provider], [], []);
        public ValueTask StartAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask StopAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Policy : IToolPolicy
    {
        public bool Current { get; set; } = true;
        public ToolPolicyDecision Decide(ToolExecutionRequest request, RegisteredTool tool) => Current ? ToolPolicyDecision.Allow : ToolPolicyDecision.Deny;
    }
    private sealed class Fence : IAgentRunFence { public void EnsureCurrent(AgentRunIdentity identity, CancellationToken token) => token.ThrowIfCancellationRequested(); }
    private sealed class Intent : IToolExecutionIntent
    {
        public Action? BeforeCommit { get; init; }
        public int Commits { get; private set; }
        public ValueTask CommitStartedAsync(ToolDescriptor tool, CancellationToken token) { BeforeCommit?.Invoke(); Commits++; return ValueTask.CompletedTask; }
    }
}
