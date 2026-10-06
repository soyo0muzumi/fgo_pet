using System.Text.Json;
using FgoPet.Extensibility;
using FgoPet.Kernel.Agent;
using Xunit;

namespace FgoPet.Agent.Tests.Tools;

public sealed class ResourceAuthorizationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Approval_freezes_resource_and_rechecks_after_intent(bool changeBefore, bool changeAfter)
    {
        var scope = new ToolScope("conversation", "role", "project");
        var provider = new Provider(ToolEffect.Command);
        var catalog = PluginCatalog.Create([new Plugin(provider)]);
        await using var runtime = new PluginRuntime(catalog);
        Assert.True((await runtime.StartAsync(default)).Succeeded);
        var registry = new ToolRegistry(catalog, runtime);
        var pipeline = new ToolExecutionPipeline(registry, new(registry, scope, new Fence()), new AskCommandPolicy(), new ApprovalBroker());
        var request = new ToolExecutionRequest(new("run", "user", scope, "model", 1), 1, new("call", "fixture.resource", "{}"));
        var intent = new Intent();
        var wait = await pipeline.AdvanceAsync(request, intent, default);
        Assert.Equal(ToolExecutionOutcomeKind.WaitingApproval, wait.Kind);
        Assert.Equal(provider.Authority, wait.ApprovalRequest!.Tool.Resource);
        Assert.Equal("root", wait.ApprovalRequest.Tool.RootAuthorizationId);
        Assert.Equal(0, intent.Commits);
        var approved = request with { Approval = wait.ApprovalRequest, CheckpointRevision = 2 };
        if (changeBefore) provider.Authority = new("root", 2);
        if (changeAfter) intent.OnCommit = () => provider.Authority = new("root", 2);
        var result = await pipeline.AdvanceAsync(approved, intent, default);
        Assert.Equal(changeBefore || changeAfter ? 0 : 1, provider.Calls);
        Assert.Equal(changeBefore ? 0 : 1, intent.Commits);
        Assert.Equal(!(changeBefore || changeAfter), result.Result!.Success);
        Assert.Equal(changeBefore ? "TOOL_APPROVAL_STALE" : changeAfter ? "TOOL_SCOPE_DENIED" : null, result.Result.ErrorCode);
    }

    [Fact]
    public async Task Read_only_resource_is_also_frozen_across_intent()
    {
        var scope = new ToolScope("conversation", "role", null);
        var provider = new Provider(ToolEffect.ReadOnly);
        var catalog = PluginCatalog.Create([new Plugin(provider)]);
        await using var runtime = new PluginRuntime(catalog);
        await runtime.StartAsync(default);
        var registry = new ToolRegistry(catalog, runtime);
        var pipeline = new ToolExecutionPipeline(registry, new(registry, scope, new Fence()));
        var intent = new Intent { OnCommit = () => provider.Authority = new("another-root", 1) };
        var result = await pipeline.AdvanceAsync(new(new("run", "user", scope, "model", 1), 1,
            new("call", "fixture.resource", "{}")), intent, default);
        Assert.Equal(0, provider.Calls);
        Assert.Equal("TOOL_SCOPE_DENIED", result.Result!.ErrorCode);
    }

    private sealed class Provider(ToolEffect effect) : IToolProvider, IToolResourceAuthorizationProvider
    {
        public ToolResourceAuthorization Authority { get; set; } = new("root", 1);
        public int Calls { get; private set; }
        public ToolDescriptor Descriptor { get; } = new("fixture.resource", "Synthetic resource", """{"type":"object","additionalProperties":false}""", effect);
        public ToolResourceAuthorization GetAuthorization(ToolScope scope) => Authority;
        public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken token)
        {
            Calls++;
            return ValueTask.FromResult(new ToolResult(true, JsonSerializer.SerializeToElement(new { ok = true }))
                { ExecutionState = effect == ToolEffect.Command ? ToolExecutionState.Committed : null });
        }
    }
    private sealed class Intent : IToolExecutionIntent
    {
        public int Commits { get; private set; }
        public Action? OnCommit { get; set; }
        public ValueTask CommitStartedAsync(ToolDescriptor descriptor, CancellationToken token)
        { Commits++; OnCommit?.Invoke(); return ValueTask.CompletedTask; }
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
