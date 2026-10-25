using System.Text.Json;
using FgoPet.Extensibility;
using FgoPet.Kernel.Agent;
using Xunit;

namespace FgoPet.Agent.Tests.Tools;

public sealed class BusinessConfirmationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Owner_draft_is_frozen_and_rechecked_before_effect(bool staleBefore, bool staleAfter)
    {
        var scope = new ToolScope("conversation", "role", null);
        var owner = new Provider();
        var catalog = PluginCatalog.Create([new Plugin(owner)]);
        await using var runtime = new PluginRuntime(catalog);
        await runtime.StartAsync(default);
        var registry = new ToolRegistry(catalog, runtime);
        var pipeline = new ToolExecutionPipeline(registry, new(registry, scope, new Fence()), new AskCommandPolicy(), new ApprovalBroker());
        var request = new ToolExecutionRequest(new("run", "user", scope, "model", 1), 1, new("call", "fixture.business", "{}"));
        var intent = new Intent();
        var wait = await pipeline.AdvanceAsync(request, intent, default);
        Assert.Equal(ToolExecutionOutcomeKind.WaitingApproval, wait.Kind);
        Assert.Equal(owner.Draft, wait.ApprovalRequest!.Tool.BusinessConfirmation);
        Assert.Equal(0, owner.Calls);
        Assert.Equal(0, intent.Commits);
        if (staleBefore) owner.Draft = owner.Draft with { Version = 2 };
        if (staleAfter) intent.OnCommit = () => owner.Draft = owner.Draft with { Version = 2 };
        var result = await pipeline.AdvanceAsync(request with { Approval = wait.ApprovalRequest, CheckpointRevision = 2 }, intent, default);
        Assert.Equal(staleBefore || staleAfter ? 0 : 1, owner.Calls);
        Assert.Equal(!(staleBefore || staleAfter), result.Result!.Success);
        Assert.Equal(staleBefore || staleAfter ? "TOOL_BUSINESS_CONFIRMATION_STALE" : null, result.Result.ErrorCode);
        Assert.Equal(1, owner.Prepares);
    }

    [Fact]
    public async Task Generic_approval_without_business_draft_cannot_invoke_owner()
    {
        var scope = new ToolScope("conversation", "role", null);
        var owner = new Provider();
        var catalog = PluginCatalog.Create([new Plugin(owner)]);
        await using var runtime = new PluginRuntime(catalog);
        await runtime.StartAsync(default);
        var registry = new ToolRegistry(catalog, runtime);
        var pipeline = new ToolExecutionPipeline(registry, new(registry, scope, new Fence()), new AskCommandPolicy(), new ApprovalBroker());
        var request = new ToolExecutionRequest(new("run", "user", scope, "model", 1), 1, new("call", "fixture.business", "{}"));
        var intent = new Intent();
        var wait = await pipeline.AdvanceAsync(request, intent, default);
        var approval = wait.ApprovalRequest! with { Tool = wait.ApprovalRequest!.Tool with { BusinessConfirmation = null } };
        var result = await pipeline.AdvanceAsync(request with { Approval = approval }, intent, default);
        Assert.Equal("TOOL_BUSINESS_CONFIRMATION_REQUIRED", result.Result!.ErrorCode);
        Assert.Equal(0, owner.Calls);
        Assert.Equal(0, intent.Commits);
    }
    private sealed class Provider : IToolProvider, IToolBusinessConfirmationProvider
    {
        public ToolBusinessConfirmation Draft { get; set; } = new("draft", 1, new string('A', 64));
        public int Calls { get; private set; }
        public int Prepares { get; private set; }
        public ToolDescriptor Descriptor { get; } = new("fixture.business", "Synthetic draft command", """{"type":"object","additionalProperties":false}""", ToolEffect.Command);
        public ToolBusinessConfirmation PrepareConfirmation(ToolInvocation invocation, CancellationToken token) { Prepares++; return Draft; }
        public void ValidateConfirmation(ToolInvocation invocation, ToolBusinessConfirmation confirmation, CancellationToken token)
        { if (confirmation != Draft) throw new ToolBusinessConfirmationException("DRAFT_STALE"); }
        public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken token)
        {
            Assert.Equal(Draft, invocation.ExecutionContext!.BusinessConfirmation);
            Calls++;
            return ValueTask.FromResult(new ToolResult(true, JsonSerializer.SerializeToElement(new { ok = true })) { ExecutionState = ToolExecutionState.Committed });
        }
    }
    private sealed class Intent : IToolExecutionIntent
    {
        public int Commits { get; private set; }
        public Action? OnCommit { get; set; }
        public ValueTask CommitStartedAsync(ToolDescriptor descriptor, CancellationToken token) { Commits++; OnCommit?.Invoke(); return ValueTask.CompletedTask; }
    }
    private sealed class Fence : IAgentRunFence { public void EnsureCurrent(AgentRunIdentity identity, CancellationToken token) => token.ThrowIfCancellationRequested(); }
    private sealed class Plugin(IToolProvider provider) : IFgoPetPlugin
    {
        public PluginManifest Manifest { get; } = new("fixture", "1.0", 1, []);
        public PluginContributions Contributions { get; } = PluginContributions.Empty with { Tools = [provider] };
        public ValueTask StartAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask StopAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
