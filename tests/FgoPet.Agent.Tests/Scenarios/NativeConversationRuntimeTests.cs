using System.Text.Json;
using FgoPet.Agent.Tests.TestDoubles;
using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Extensibility;
using FgoPet.Kernel.Agent;
using Xunit;

namespace FgoPet.Agent.Tests.Scenarios;

public sealed class NativeConversationRuntimeTests
{
    private static readonly ToolScope Scope = new("conversation", "role", "project");

    [Fact]
    public async Task Synchronous_disposal_fences_admission_and_async_shutdown_drains_waiting_run()
    {
        await using var host = await RuntimeHost.CreateAsync();
        await using var runtime = host.CreateRuntime();
        var delivery = new DeliveryRecorder();
        var waiting = await runtime.StartAsync(Request("run-sync-dispose"),
            token => Model(new ScriptedAgentModelStep(Ask()), token), () => { }, delivery.DeliverAsync, default);
        Assert.Equal(ConversationSendStatus.WaitingUserInput, waiting.Status);
        runtime.Dispose();
        var error = await Assert.ThrowsAsync<AgentStateException>(() => runtime.StartAsync(Request("run-after-dispose"),
            token => Model(new ScriptedAgentModelStep(Final("must not send")), token), () => { }, delivery.DeliverAsync, default).AsTask());
        Assert.Equal("RUN_HOST_CLOSED", error.Code);
        await runtime.DisposeAsync();
        Assert.False(runtime.HasActiveConversation(Scope.ConversationId));
        Assert.Equal(AgentRunStatus.Cancelled, (await host.Store.LoadAsync("run-sync-dispose", default))!.Snapshot.Status);
        Assert.Empty(delivery.Results);
    }

    [Fact]
    public async Task Only_persisted_completed_runs_are_delivered_and_delivery_runs_once()
    {
        await using var host = await RuntimeHost.CreateAsync();
        await using var runtime = host.CreateRuntime();
        var delivery = new DeliveryRecorder();
        var model = new ScriptedAgentModelStep(Final("persisted answer"));
        var request = Request("run-persisted");

        var result = await runtime.StartAsync(request, token => Model(model, token), () => { },
            delivery.DeliverAsync, default);

        Assert.Equal(ConversationSendStatus.Completed, result.Status);
        Assert.Equal(request.Identity.RunId, result.RunId);
        var delivered = Assert.Single(delivery.Results);
        Assert.True(delivered.StatePersisted);
        Assert.Equal(AgentRunStatus.Completed, delivered.Snapshot.Status);
        Assert.Equal("persisted answer", delivered.FinalText);

        var secondDelivery = new DeliveryRecorder();
        var faultStore = new InMemoryAgentRunStore();
        await using var failedRuntime = host.CreateRuntime(
            new ScriptedFaultingRunStore(faultStore, AgentEventKind.RunCompleted));
        var unpersisted = await failedRuntime.StartAsync(Request("run-unpersisted"),
            token => Model(new ScriptedAgentModelStep(Final("must not deliver")), token), () => { },
            secondDelivery.DeliverAsync, default);

        Assert.Equal(ConversationSendStatus.Failed, unpersisted.Status);
        Assert.Equal("run-unpersisted", unpersisted.RunId);
        Assert.Empty(secondDelivery.Results);
        var unpersistedCheckpoint = await faultStore.LoadAsync("run-unpersisted", default);
        Assert.NotEqual(AgentRunStatus.Completed, unpersistedCheckpoint!.Snapshot.Status);
        Assert.Null(unpersistedCheckpoint.FinalText);
    }

    [Fact]
    public async Task Waiting_input_returns_run_id_and_valid_reply_continues_same_run_to_delivery()
    {
        await using var host = await RuntimeHost.CreateAsync();
        await using var runtime = host.CreateRuntime();
        var delivery = new DeliveryRecorder();
        var model = new ScriptedAgentModelStep(Ask(), Final("answered and completed"));
        var request = Request("run-input");

        var waiting = await runtime.StartAsync(request, token => Model(model, token), () => { },
            delivery.DeliverAsync, default);

        Assert.Equal(ConversationSendStatus.WaitingUserInput, waiting.Status);
        Assert.Equal(request.Identity.RunId, waiting.RunId);
        Assert.Equal(Scope.ConversationId, waiting.ConversationId);
        Assert.Empty(delivery.Results);
        var interaction = (await runtime.GetInteractionAsync(waiting.RunId!, Scope, default))!;
        var input = Assert.IsType<UserInputRequest>(interaction.Input);
        Assert.Null(interaction.Approval);
        var reply = new UserInputReply(waiting.RunId!, input.Binding.RequestId, input.Binding.WaitingRevision,
            [new QuestionAnswer("choice", [0], null)]);

        var completed = await runtime.ResumeInputAsync(reply, Scope, default);

        Assert.Equal(ConversationSendStatus.Completed, completed.Status);
        Assert.Equal(waiting.RunId, completed.RunId);
        var delivered = Assert.Single(delivery.Results);
        Assert.True(delivered.StatePersisted);
        Assert.Equal(2, model.Requests.Count);
        Assert.Single((await host.Store.LoadAsync(waiting.RunId!, default))!.ResolvedInputs);
    }

    [Fact]
    public async Task Waiting_approval_rejects_old_scope_without_consuming_card_then_continues_same_run()
    {
        var command = CommittedCommand();
        await using var host = await RuntimeHost.CreateAsync(command);
        await using var runtime = host.CreateRuntime(policy: new AskCommandPolicy());
        var delivery = new DeliveryRecorder();
        var model = new ScriptedAgentModelStep(Call("command", "fixture.command", "{}"), Final("approved and completed"));
        var request = Request("run-approval");

        var waiting = await runtime.StartAsync(request, token => Model(model, token), () => { },
            delivery.DeliverAsync, default);

        Assert.Equal(ConversationSendStatus.WaitingApproval, waiting.Status);
        Assert.Equal(request.Identity.RunId, waiting.RunId);
        Assert.Empty(delivery.Results);
        Assert.Empty(command.Invocations);
        var interaction = (await runtime.GetInteractionAsync(waiting.RunId!, Scope, default))!;
        var approval = Assert.IsType<ApprovalRequest>(interaction.Approval);
        Assert.Null(interaction.Input);
        var reply = new ApprovalReply(waiting.RunId!, approval.Binding.RequestId,
            approval.Binding.WaitingRevision, ApprovalDecision.Allow);
        var oldScope = Scope with { ProjectId = "previous-project" };

        var rejected = await Assert.ThrowsAsync<AgentStateException>(() =>
            runtime.ResumeApprovalAsync(reply, oldScope, default).AsTask());

        Assert.Equal("RUN_INTERACTION_REJECTED", rejected.Code);
        var stillWaiting = (await runtime.GetInteractionAsync(waiting.RunId!, Scope, default))!;
        Assert.Equal(approval.Binding, stillWaiting.Approval!.Binding);
        Assert.Empty(delivery.Results);
        Assert.Empty(command.Invocations);

        var completed = await runtime.ResumeApprovalAsync(reply, Scope, default);

        Assert.Equal(ConversationSendStatus.Completed, completed.Status);
        Assert.Equal(waiting.RunId, completed.RunId);
        Assert.Single(command.Invocations);
        Assert.True(Assert.Single(delivery.Results).StatePersisted);
    }

    [Fact]
    public async Task Invalidated_trusted_scope_check_stops_model_provider_tool_and_delivery()
    {
        var reader = new RecordingToolProvider("fixture.read");
        await using var host = await RuntimeHost.CreateAsync(reader);
        await using var runtime = host.CreateRuntime();
        var delivery = new DeliveryRecorder();
        var model = new ScriptedAgentModelStep(Call("read", "fixture.read", "{}"), Final("unreachable"));
        var checks = 0;

        var result = await runtime.StartAsync(Request("run-invalidated"), token => Model(model, token),
            () =>
            {
                checks++;
                throw new AgentStateException("RUN_SCOPE_CHANGED");
            }, delivery.DeliverAsync, default);

        Assert.True(checks > 0);
        Assert.Equal(ConversationSendStatus.Failed, result.Status);
        Assert.Equal("run-invalidated", result.RunId);
        Assert.Empty(model.Requests);
        Assert.Empty(reader.Invocations);
        Assert.Empty(delivery.Results);
    }

    [Fact]
    public async Task Command_without_host_policy_is_denied_without_invoking_provider()
    {
        var command = CommittedCommand();
        await using var host = await RuntimeHost.CreateAsync(command);
        await using var runtime = host.CreateRuntime();
        var delivery = new DeliveryRecorder();
        var model = new ScriptedAgentModelStep(Call("command", "fixture.command", "{}"), Final("denied safely"));

        var result = await runtime.StartAsync(Request("run-default-deny"), token => Model(model, token), () => { },
            delivery.DeliverAsync, default);

        Assert.Equal(ConversationSendStatus.Completed, result.Status);
        Assert.Empty(command.Invocations);
        var delivered = Assert.Single(delivery.Results);
        Assert.True(delivered.StatePersisted);
        using var observation = JsonDocument.Parse(model.Requests[1].Messages.Single(message =>
            message.Role == ModelMessageRole.Tool).Content);
        Assert.Equal("TOOL_AUTHORIZATION_DENIED", observation.RootElement.GetProperty("errorCode").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancel_or_shutdown_clears_wait_and_allows_next_run_in_same_conversation(bool shutdown)
    {
        await using var host = await RuntimeHost.CreateAsync();
        var runtime = host.CreateRuntime();
        var delivery = new DeliveryRecorder();
        var waiting = await runtime.StartAsync(Request("run-waiting"),
            token => Model(new ScriptedAgentModelStep(Ask(), Final("must not deliver")), token), () => { },
            delivery.DeliverAsync, default);

        Assert.Equal(ConversationSendStatus.WaitingUserInput, waiting.Status);
        Assert.Empty(delivery.Results);
        Assert.NotNull(await runtime.GetInteractionAsync(waiting.RunId!, Scope, default));

        if (shutdown)
            await runtime.DisposeAsync();
        else
            Assert.True(await runtime.CancelAsync(waiting.RunId!, Scope, default));

        var cancelled = await host.Store.LoadAsync(waiting.RunId!, default);
        Assert.Equal(AgentRunStatus.Cancelled, cancelled!.Snapshot.Status);
        Assert.Null(cancelled.Waiting);
        Assert.Null(await runtime.GetInteractionAsync(waiting.RunId!, Scope, default));
        Assert.Empty(delivery.Results);

        if (shutdown)
        {
            await using var replacement = host.CreateRuntime();
            await AssertNextRunCompletes(replacement);
        }
        else
        {
            await AssertNextRunCompletes(runtime);
        }

        await runtime.DisposeAsync();
    }

    private static async Task AssertNextRunCompletes(NativeConversationRuntime runtime)
    {
        var delivery = new DeliveryRecorder();
        var model = new ScriptedAgentModelStep(Final("next run completed"));
        var result = await runtime.StartAsync(Request("run-next"), token => Model(model, token), () => { },
            delivery.DeliverAsync, default);
        Assert.Equal(ConversationSendStatus.Completed, result.Status);
        Assert.Equal("run-next", result.RunId);
        Assert.True(Assert.Single(delivery.Results).StatePersisted);
    }

    private static AgentRunRequest Request(string runId) => new(
        new AgentRunIdentity(runId, $"user-{runId}", Scope, "model-revision", 1), new(),
        [new(ModelMessageRole.User, "synthetic request", [])]);

    private static ValueTask<IAgentModelStep> Model(ScriptedAgentModelStep model, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IAgentModelStep>(model);
    }

    private static ModelStepResponse Ask() => Call("ask", "user.ask",
        """{"questions":[{"id":"choice","question":"Choose one","options":["A","B"]}]}""");

    private static ModelStepResponse Call(string id, string name, string arguments) =>
        new(new(ModelMessageRole.Assistant, string.Empty, [new ModelToolCall(id, name, arguments)]), "tool_calls", true);

    private static ModelStepResponse Final(string content) =>
        new(new(ModelMessageRole.Assistant, content, []), "stop", true);

    private static RecordingToolProvider CommittedCommand()
    {
        var provider = new RecordingToolProvider("fixture.command", ToolEffect.Command,
            """{"type":"object","properties":{},"additionalProperties":false}""");
        provider.Handler = (_, token) =>
        {
            token.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new ToolResult(true, JsonSerializer.SerializeToElement(new { committed = true }))
                { ExecutionState = ToolExecutionState.Committed });
        };
        return provider;
    }

    private sealed class DeliveryRecorder
    {
        public List<AgentRunResult> Results { get; } = [];
        public ValueTask<ConversationSendResult> DeliverAsync(AgentRunResult result, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Results.Add(result);
            return ValueTask.FromResult(new ConversationSendResult(ConversationSendStatus.Completed,
                result.Snapshot.Identity.Scope.ConversationId, "assistant-final"));
        }
    }

    private sealed class RuntimeHost(PluginRuntime plugins, ToolRegistry tools, InMemoryAgentRunStore store,
        TestTimeProvider time, NativeAgentExtensions extensions) : IAsyncDisposable
    {
        public InMemoryAgentRunStore Store { get; } = store;

        public static async Task<RuntimeHost> CreateAsync(params IToolProvider[] providers)
        {
            var catalog = PluginCatalog.Create([new TestPlugin(providers)]);
            var plugins = new PluginRuntime(catalog);
            var activation = await plugins.StartAsync(default);
            if (!activation.Succeeded) throw new InvalidOperationException("TEST_PLUGIN_ACTIVATION_FAILED");
            var tools = new ToolRegistry(catalog, plugins);
            var time = new TestTimeProvider();
            var extensions = new NativeAgentExtensions(Questions: new UserInputBroker(time),
                Approvals: new ApprovalBroker(time));
            return new(plugins, tools, new InMemoryAgentRunStore(), time, extensions);
        }

        public NativeConversationRuntime CreateRuntime(IAgentRunStore? runStore = null, IToolPolicy? policy = null) =>
            new(runStore ?? Store, tools, extensions, time, policy);

        public ValueTask DisposeAsync() => plugins.DisposeAsync();
    }

    private sealed class TestPlugin(params IToolProvider[] providers) : IFgoPetPlugin
    {
        public PluginManifest Manifest { get; } = new("fixture.runtime", "1.0.0", 1, []);
        public PluginContributions Contributions { get; } = PluginContributions.Empty with
        {
            Tools = [.. providers]
        };

        public ValueTask StartAsync(CancellationToken stoppingToken) => ValueTask.CompletedTask;
        public ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 6, 0, 0, 0, TimeSpan.Zero);
    }
}
