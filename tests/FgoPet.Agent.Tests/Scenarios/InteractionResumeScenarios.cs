using System.Collections.Immutable;
using System.Text.Json;
using FgoPet.Agent.Tests.TestDoubles;
using FgoPet.Extensibility;
using FgoPet.Kernel.Agent;
using Xunit;

namespace FgoPet.Agent.Tests.Scenarios;

public sealed class InteractionResumeScenarios
{
    [Theory]
    [InlineData(ApprovalDecision.Allow, 1)]
    [InlineData(ApprovalDecision.Deny, 0)]
    public async Task Approval_continues_original_serial_group_without_recharging_or_repeating_a(ApprovalDecision decision, int writes)
    {
        await using var host = await Host.Create(Calls(Read("a"), Write("b"), Read("c")), Final());
        var waiting = await host.Coordinator.StartAsync(Request(), default);
        Assert.Equal(AgentRunStatus.WaitingApproval, waiting.Snapshot.Status);
        Assert.Equal(new[] { "a" }, host.Invocations);
        Assert.Single(host.Model.Requests);
        var approval = (await host.Coordinator.GetInteractionAsync("run", Scope, default))!.Approval!;
        Assert.Equal(waiting.Snapshot.Revision, approval.Binding.WaitingRevision);
        var reply = new ApprovalReply("run", approval.Binding.RequestId, approval.Binding.WaitingRevision, decision);
        var final = await host.Coordinator.ResumeApprovalAsync(reply, Scope, default);
        Assert.Equal(AgentRunStatus.Completed, final.Snapshot.Status);
        Assert.Equal("run", final.Snapshot.Identity.RunId);
        Assert.Equal(3, final.Snapshot.ToolCalls);
        Assert.Equal(writes, host.WriteProvider.Calls);
        Assert.Equal(writes == 1 ? new[] { "a", "b", "c" } : new[] { "a", "c" }, host.Invocations);
        Assert.Equal(new[] { "a", "b", "c" }, host.Model.Requests[1].Messages
            .Where(message => message.Role == ModelMessageRole.Tool).Select(message => message.ToolCallId));
        await Assert.ThrowsAsync<AgentStateException>(() => host.Coordinator.ResumeApprovalAsync(reply, Scope, default).AsTask());
        Assert.Equal(writes, host.WriteProvider.Calls);
    }

    [Fact]
    public async Task Expiry_is_a_failed_observation_and_never_an_approval()
    {
        await using var host = await Host.Create(Calls(Read("a"), Write("b"), Read("c")), Final());
        var waiting = await host.Coordinator.StartAsync(Request(), default);
        var approval = (await host.Coordinator.GetInteractionAsync("run", Scope, default))!.Approval!;
        host.Clock.Now = approval.Binding.ExpiresAt;
        await Assert.ThrowsAsync<AgentStateException>(() => host.Coordinator.ResumeApprovalAsync(
            new("run", approval.Binding.RequestId, waiting.Snapshot.Revision, ApprovalDecision.Allow), Scope, default).AsTask());
        var final = await host.Coordinator.ExpireInteractionAsync("run", Scope, default);
        Assert.Equal(AgentRunStatus.Completed, final.Snapshot.Status);
        Assert.Equal(0, host.WriteProvider.Calls);
        Assert.Equal(new[] { "a", "c" }, host.Invocations);
        Assert.Contains(host.Model.Requests[1].Messages, message => message.ToolCallId == "b"
            && message.Content.Contains("TOOL_APPROVAL_EXPIRED", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("scope")]
    [InlineData("request")]
    public async Task Invalid_reply_does_not_consume_or_terminate_live_wait(string mismatch)
    {
        await using var host = await Host.Create(Calls(Write("b")), Final());
        var waiting = await host.Coordinator.StartAsync(Request(), default);
        var approval = (await host.Coordinator.GetInteractionAsync("run", Scope, default))!.Approval!;
        var reply = new ApprovalReply("run", approval.Binding.RequestId, waiting.Snapshot.Revision, ApprovalDecision.Allow);
        var wrong = mismatch == "revision" ? reply with { ExpectedRevision = reply.ExpectedRevision - 1 }
            : mismatch == "request" ? reply with { RequestId = "obsolete" } : reply;
        await Assert.ThrowsAsync<AgentStateException>(() => host.Coordinator.ResumeApprovalAsync(wrong,
            mismatch == "scope" ? Scope with { RoleId = "other" } : Scope, default).AsTask());
        Assert.Equal(waiting.Snapshot.Revision, (await host.Store.LoadAsync("run", default))!.Snapshot.Revision);
        Assert.Equal(0, host.WriteProvider.Calls);
        Assert.Equal(AgentRunStatus.Completed, (await host.Coordinator.ResumeApprovalAsync(reply, Scope, default)).Snapshot.Status);
        Assert.Equal(1, host.WriteProvider.Calls);
    }

    [Fact]
    public async Task Question_answer_does_not_approve_later_command_and_old_request_token_is_released()
    {
        await using var host = await Host.Create(Calls(Ask("q"), Write("b"), Read("c")), Final());
        using var original = new CancellationTokenSource();
        var waiting = await host.Coordinator.StartAsync(Request(), original.Token);
        var question = (await host.Coordinator.GetInteractionAsync("run", Scope, default))!.Input!;
        original.Cancel();
        var reply = new UserInputReply("run", question.Binding.RequestId, waiting.Snapshot.Revision,
            [new("choice", [], "可以")]);
        var approvedWait = await host.Coordinator.ResumeInputAsync(reply, Scope, default);
        Assert.Equal(AgentRunStatus.WaitingApproval, approvedWait.Snapshot.Status);
        Assert.Equal(0, host.WriteProvider.Calls);
        Assert.Single(host.Model.Requests);
        await Assert.ThrowsAsync<AgentStateException>(() => host.Coordinator.ResumeInputAsync(reply, Scope, default).AsTask());
        Assert.Single((await host.Store.LoadAsync("run", default))!.ResolvedInputs);
        var approval = (await host.Coordinator.GetInteractionAsync("run", Scope, default))!.Approval!;
        var completed = await host.Coordinator.ResumeApprovalAsync(new("run", approval.Binding.RequestId,
            approval.Binding.WaitingRevision, ApprovalDecision.Deny), Scope, default);
        Assert.Equal(AgentRunStatus.Completed, completed.Snapshot.Status);
        Assert.Equal(3, completed.Snapshot.ToolCalls);
        Assert.Contains(host.Model.Requests[1].Messages, message => message.ToolCallId == "q"
            && message.Content.Contains("可以", StringComparison.Ordinal));
        Assert.Equal(0, host.WriteProvider.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancel_and_shutdown_invalidate_waiting_card(bool shutdown)
    {
        await using var host = await Host.Create(Calls(Write("b")), Final());
        await host.Coordinator.StartAsync(Request(), default);
        var approval = (await host.Coordinator.GetInteractionAsync("run", Scope, default))!.Approval!;
        if (shutdown) await host.Coordinator.DisposeAsync();
        else Assert.True(await host.Coordinator.CancelAsync("run", Scope, default));
        Assert.Null(await host.Coordinator.GetInteractionAsync("run", Scope, default));
        await Assert.ThrowsAsync<AgentStateException>(() => host.Coordinator.ResumeApprovalAsync(new("run", approval.Binding.RequestId,
            approval.Binding.WaitingRevision, ApprovalDecision.Allow), Scope, default).AsTask());
        Assert.Equal(0, host.WriteProvider.Calls);
        Assert.Equal(AgentRunStatus.Cancelled, (await host.Store.LoadAsync("run", default))!.Snapshot.Status);
    }

    [Fact]
    public async Task Failed_consumption_never_starts_command()
    {
        await using var host = await Host.Create(Calls(Write("b"), Read("c")), Final(), failAt: AgentEventKind.ApprovalResolved);
        var waiting = await host.Coordinator.StartAsync(Request(), default);
        var approval = (await host.Coordinator.GetInteractionAsync("run", Scope, default))!.Approval!;
        var result = await host.Coordinator.ResumeApprovalAsync(new("run", approval.Binding.RequestId,
            waiting.Snapshot.Revision, ApprovalDecision.Allow), Scope, default);
        Assert.Equal(AgentRunStatus.Failed, result.Snapshot.Status);
        Assert.False(result.StatePersisted);
        Assert.Empty(host.Invocations);
        Assert.Single(host.Model.Requests);
        Assert.Equal(AgentRunStatus.WaitingApproval, (await host.Store.LoadAsync("run", default))!.Snapshot.Status);
    }

    [Fact]
    public async Task Committed_effect_with_projection_failure_remains_visible_in_observation()
    {
        await using var host = await Host.Create(Calls(Write("b")), Final());
        host.WriteProvider.OversizedResult = true;
        var waiting = await host.Coordinator.StartAsync(Request(), default);
        var approval = (await host.Coordinator.GetInteractionAsync("run", Scope, default))!.Approval!;
        var final = await host.Coordinator.ResumeApprovalAsync(new("run", approval.Binding.RequestId,
            waiting.Snapshot.Revision, ApprovalDecision.Allow), Scope, default);
        Assert.Equal(AgentRunStatus.Completed, final.Snapshot.Status);
        var observation = host.Model.Requests[1].Messages.Single(message => message.ToolCallId == "b");
        using var json = JsonDocument.Parse(observation.Content);
        Assert.False(json.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("Committed", json.RootElement.GetProperty("executionState").GetString());
        Assert.Equal(1, host.WriteProvider.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Maximum_unicode_answer_remains_a_valid_paired_observation(bool supplementary)
    {
        await using var host = await Host.Create(Calls(Ask("q")), Final());
        var waiting = await host.Coordinator.StartAsync(Request(), default);
        var question = (await host.Coordinator.GetInteractionAsync("run", Scope, default))!.Input!;
        var text = supplementary ? string.Concat(Enumerable.Repeat("😀", 6000)) : new string('中', 12000);
        var final = await host.Coordinator.ResumeInputAsync(new("run", question.Binding.RequestId,
            waiting.Snapshot.Revision, [new("choice", [], text)]), Scope, default);
        Assert.Equal(AgentRunStatus.Completed, final.Snapshot.Status);
        ModelProtocol.ValidateTranscript(host.Model.Requests[1].Messages);
        using var json = JsonDocument.Parse(host.Model.Requests[1].Messages.Single(m => m.ToolCallId == "q").Content);
        Assert.Equal(text, json.RootElement.GetProperty("payload").GetProperty("answers")[0].GetProperty("text").GetString());
    }

    private static readonly ToolScope Scope = new("conversation", "role", "project");
    private static AgentRunRequest Request() => new(new("run", "user", Scope, "route", 1), new(),
        [new(ModelMessageRole.User, "synthetic request", [])]);
    private static ModelToolCall Read(string id) => new(id, "fixture.read", "{}");
    private static ModelToolCall Write(string id) => new(id, "fixture.write", "{}");
    private static ModelToolCall Ask(string id) => new(id, "user.ask", """{"questions":[{"id":"choice","question":"选择内容","options":["选项"]}]}""");
    private static ModelStepResponse Calls(params ModelToolCall[] calls) => new(new(ModelMessageRole.Assistant, "", [.. calls]), "tool_calls", true);
    private static ModelStepResponse Final() => new(new(ModelMessageRole.Assistant, "done", []), "stop", true);
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 6, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Provider(string name, ToolEffect effect, List<string> invocations) : IToolProvider
    {
        public int Calls { get; private set; }
        public bool OversizedResult { get; set; }
        public ToolDescriptor Descriptor { get; } = new(name, "Synthetic fixture", """{"type":"object","additionalProperties":false}""", effect);
        public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Calls++;
            invocations.Add(invocation.ExecutionContext!.CallId);
            return ValueTask.FromResult(new ToolResult(true, JsonSerializer.SerializeToElement(new
                { value = OversizedResult ? new string('x', 65537) : "fixture" }))
                { ExecutionState = effect == ToolEffect.Command ? ToolExecutionState.Committed : null });
        }
    }
    private sealed class Plugin(params IToolProvider[] providers) : IFgoPetPlugin
    {
        public PluginManifest Manifest { get; } = new("fixture", "1.0", 1, []);
        public PluginContributions Contributions { get; } = PluginContributions.Empty with { Tools = [.. providers] };
        public ValueTask StartAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask StopAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Host : IAsyncDisposable
    {
        public Clock Clock { get; } = new();
        public InMemoryAgentRunStore Store { get; } = new();
        public List<string> Invocations => _recorded;
        public required Provider WriteProvider { get; init; }
        public required ScriptedAgentModelStep Model { get; init; }
        public required PluginRuntime Runtime { get; init; }
        public required AgentRunCoordinator Coordinator { get; set; }
        public static async Task<Host> Create(ModelStepResponse first, ModelStepResponse final, AgentEventKind? failAt = null)
        {
            var invocations = new List<string>();
            var writer = new Provider("fixture.write", ToolEffect.Command, invocations);
            var catalog = PluginCatalog.Create([new Plugin(new Provider("fixture.read", ToolEffect.ReadOnly, invocations), writer)]);
            var runtime = new PluginRuntime(catalog);
            await runtime.StartAsync(default);
            var host = new Host { WriteProvider = writer, Model = new(first, final), Runtime = runtime, Coordinator = null! };
            var registry = new ToolRegistry(catalog, runtime);
            var approval = new ApprovalBroker(host.Clock, TimeSpan.FromMinutes(15));
            var questions = new UserInputBroker(host.Clock);
            var fence = new ScriptedRunFence();
            IAgentRunStore store = failAt is null ? host.Store : new ScriptedFaultingRunStore(host.Store, failAt.Value);
            host.Coordinator = new(store, (_, _) => ValueTask.FromResult<IAgentModelStep>(host.Model), new StepEnvironmentBuilder(registry.GetTools),
                new ToolExecutionPipeline(registry, new(registry, Scope, fence), new AskCommandPolicy(), approval, "root"),
                fence, host.Clock, new(Questions: questions, Approvals: approval));
            host._recorded = invocations;
            return host;
        }
        private List<string> _recorded = [];
        public async ValueTask DisposeAsync() { await Coordinator.DisposeAsync(); await Runtime.DisposeAsync(); }
    }
}
