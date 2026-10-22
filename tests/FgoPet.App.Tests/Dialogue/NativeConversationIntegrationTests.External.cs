using System.Windows.Threading;
using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Packs;
using FgoPet.Dialogue.Contracts;
using FgoPet.Extensibility;
using FgoPet.Kernel.Agent;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

public sealed partial class NativeConversationIntegrationTests
{
    [Fact]
    public Task External_capture_requires_an_existing_conversation_without_creating_one() => RunInputChatTest(async () =>
    {
        var provider = new ScriptedProvider();
        await using var host = await TestHost.CreateAsync(provider, null, new RecordingObserver());
        using var model = new ConversationViewModel(host.Orchestrator, host.Settings);
        model.SetActiveServant(RoleId);
        var gateway = new ExternalConversationGateway(model, Dispatcher.CurrentDispatcher);

        Assert.Null(await gateway.CaptureCurrentBindingAsync());
        Assert.Empty(model.CurrentConversationId);
        Assert.Empty(provider.Requests);

        var binding = BindExternalConversation(host, model);
        Assert.Equal(binding, await gateway.CaptureCurrentBindingAsync());
        Assert.Empty(host.Conversations.LoadMessages(binding.ConversationId, RoleId));
    });

    [Fact]
    public Task External_send_uses_shared_history_and_observer_preserves_draft_and_returns_only_final_text() => RunInputChatTest(async () =>
    {
        var final = new ProviderTurn([new ChatStreamChunk("Synthetic final answer", IsComplete: true,
            FinishReason: "stop", ReasoningDelta: "synthetic-private-reasoning")]);
        var provider = new ScriptedProvider(final);
        var observer = new RecordingObserver();
        await using var host = await TestHost.CreateAsync(provider, new RecordingTool(ToolEffect.ReadOnly), observer,
            TestSettingsStore.Create(32768));
        using var model = new ConversationViewModel(host.Orchestrator, host.Settings);
        var binding = BindExternalConversation(host, model);
        var gateway = new ExternalConversationGateway(model, Dispatcher.CurrentDispatcher);
        model.InputText = "Unsent desktop draft";

        // The inbound transport runs off the UI thread; the gateway marshals the shared owner.
        var result = await Task.Run(() => gateway.ExchangeTextAsync(binding, "Synthetic remote user message"));

        Assert.Equal(ExternalConversationStatus.Completed, result.Status);
        Assert.Equal("Synthetic final answer", result.Text);
        Assert.Null(result.ErrorCode);
        Assert.Equal("Unsent desktop draft", model.InputText);
        Assert.False(model.IsStreaming);
        Assert.Contains(model.Turns, turn => !turn.IsAssistant && turn.Text == "Synthetic remote user message");
        Assert.Contains(model.Turns, turn => turn.MessageId == result.MessageId && turn.Text == result.Text);
        var messages = host.Conversations.LoadMessages(binding.ConversationId, RoleId);
        Assert.Equal(2, messages.Count);
        Assert.Equal(result.Text, messages.Single(message => message.MessageId == result.MessageId).Text);
        Assert.DoesNotContain(messages, message => message.Text.Contains("synthetic-private-reasoning", StringComparison.Ordinal));
        var observed = Assert.Single(observer.Turns);
        Assert.Equal(new ToolScope(binding.ConversationId, RoleId, ProjectId), observed.Scope);
        Assert.Equal("Synthetic remote user message", observed.UserText);
        Assert.Equal(result.MessageId, observed.AssistantMessageId);
    });

    [Theory]
    [InlineData("role")]
    [InlineData("conversation")]
    [InlineData("project")]
    public Task External_send_rejects_binding_mismatch_before_persistence_or_dispatch(string mismatch) => RunInputChatTest(async () =>
    {
        var provider = new ScriptedProvider();
        await using var host = await TestHost.CreateAsync(provider, null, new RecordingObserver());
        using var model = new ConversationViewModel(host.Orchestrator, host.Settings);
        var binding = BindExternalConversation(host, model);
        var incorrect = mismatch switch
        {
            "role" => binding with { RoleId = "other-role" },
            "conversation" => binding with { ConversationId = "other-conversation" },
            _ => binding with { ProjectId = "other-project" },
        };
        var gateway = new ExternalConversationGateway(model, Dispatcher.CurrentDispatcher);

        var result = await gateway.ExchangeTextAsync(incorrect, "Must not be dispatched");

        Assert.Equal(ExternalConversationStatus.ScopeChanged, result.Status);
        Assert.Equal("CHAT_EXTERNAL_SCOPE_CHANGED", result.ErrorCode);
        Assert.Null(result.Text);
        Assert.Empty(provider.Requests);
        Assert.Empty(host.Conversations.LoadMessages(binding.ConversationId, RoleId));
        Assert.Equal(binding, await gateway.CaptureCurrentBindingAsync());
    });

    [Fact]
    public Task External_busy_rejection_and_its_cancelled_token_do_not_cancel_a_desktop_send() => RunInputChatTest(async () =>
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ScriptedProvider(ProviderTurn.BlockedFinal("Desktop answer", gate));
        await using var host = await TestHost.CreateAsync(provider, new RecordingTool(ToolEffect.ReadOnly), new RecordingObserver(),
            TestSettingsStore.Create(32768));
        using var model = new ConversationViewModel(host.Orchestrator, host.Settings);
        var binding = BindExternalConversation(host, model);
        var gateway = new ExternalConversationGateway(model, Dispatcher.CurrentDispatcher);
        using var rejectedCancellation = new CancellationTokenSource();
        model.InputText = "Desktop request";
        var desktopSend = model.SendCommand.ExecuteAsync(null);
        try
        {
            await provider.FirstRequestReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var result = await gateway.ExchangeTextAsync(binding, "Rejected remote request", rejectedCancellation.Token);
            Assert.Equal(ExternalConversationStatus.Busy, result.Status);
            Assert.Equal("CHAT_EXTERNAL_BUSY", result.ErrorCode);
            rejectedCancellation.Cancel();
        }
        finally { gate.TrySetResult(true); }
        await desktopSend;

        Assert.Contains(model.Turns, turn => turn.IsAssistant && turn.Text == "Desktop answer");
        Assert.DoesNotContain(host.Conversations.LoadMessages(binding.ConversationId, RoleId),
            message => message.Text == "Rejected remote request");
        Assert.False(model.IsStreaming);
    });

    [Fact]
    public Task External_cancelled_send_releases_only_its_run_and_keeps_the_desktop_draft() => RunInputChatTest(async () =>
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finalRequested = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ScriptedProvider(ProviderTurn.BlockedFinal("Cancelled answer", gate), ProviderTurn.Final("Next answer"));
        provider.RequestReceived = request => { if (request.IsAgentRequest && request.Tools is null) finalRequested.TrySetResult(true); };
        await using var host = await TestHost.CreateAsync(provider, new RecordingTool(ToolEffect.ReadOnly), new RecordingObserver(),
            TestSettingsStore.Create(32768));
        using var model = new ConversationViewModel(host.Orchestrator, host.Settings);
        var binding = BindExternalConversation(host, model);
        var gateway = new ExternalConversationGateway(model, Dispatcher.CurrentDispatcher);
        using var cancellation = new CancellationTokenSource();
        model.InputText = "Preserved draft";
        var send = gateway.ExchangeTextAsync(binding, "Remote request to cancel", cancellation.Token);
        try
        {
            await finalRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
        }
        finally { gate.TrySetResult(true); }

        Assert.Equal(ExternalConversationStatus.Cancelled, (await send).Status);
        Assert.False(model.IsStreaming);
        Assert.False(model.HasPendingRun);
        Assert.Equal("Preserved draft", model.InputText);
        Assert.DoesNotContain(host.Conversations.LoadMessages(binding.ConversationId, RoleId),
            message => message.Role == ChatMessageRole.Assistant && message.Status == ChatMessageStatus.Completed);

        var next = await gateway.ExchangeTextAsync(binding, "Next remote request");
        Assert.Equal(ExternalConversationStatus.Completed, next.Status);
        Assert.Equal("Next answer", next.Text);
    });

    [Fact]
    public Task External_inflight_result_is_suppressed_after_same_conversation_is_reopened() => RunInputChatTest(async () =>
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ScriptedProvider(ProviderTurn.BlockedFinal("Stale external answer", gate));
        await using var host = await TestHost.CreateAsync(provider, new RecordingTool(ToolEffect.ReadOnly), new RecordingObserver(),
            TestSettingsStore.Create(32768));
        using var model = new ConversationViewModel(host.Orchestrator, host.Settings);
        var binding = BindExternalConversation(host, model);
        var gateway = new ExternalConversationGateway(model, Dispatcher.CurrentDispatcher);
        var send = gateway.ExchangeTextAsync(binding, "Remote request before navigation");
        try
        {
            await provider.FirstRequestReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            model.SetActiveConversation(binding.ConversationId);
        }
        finally { gate.TrySetResult(true); }

        var result = await send;
        Assert.Equal(ExternalConversationStatus.ScopeChanged, result.Status);
        Assert.Null(result.Text);
        Assert.Equal(binding, await gateway.CaptureCurrentBindingAsync());
        Assert.DoesNotContain(model.Turns, turn => turn.IsAssistant && turn.Text == "Stale external answer");
    });

    [Theory]
    [InlineData("2", true)]
    [InlineData("Synthetic free answer", false)]
    public Task External_text_reply_resumes_its_original_single_question_and_preserves_draft(string reply, bool selectedOption) => RunInputChatTest(async () =>
    {
        var provider = new ScriptedProvider(ExternalQuestionTurn(), ProviderTurn.Final("Clarified answer"));
        var observer = new RecordingObserver();
        await using var host = await TestHost.CreateAsync(provider, new RecordingTool(ToolEffect.ReadOnly), observer,
            TestSettingsStore.Create(32768), questions: true);
        using var model = new ConversationViewModel(host.Orchestrator, host.Settings);
        var binding = BindExternalConversation(host, model);
        var gateway = new ExternalConversationGateway(model, Dispatcher.CurrentDispatcher);
        model.InputText = "Separate desktop draft";

        var waiting = await gateway.ExchangeTextAsync(binding, "Ask me to choose");
        Assert.Equal(ExternalConversationStatus.WaitingInput, waiting.Status);
        Assert.Contains("1. First", waiting.Text);
        Assert.Contains("2. Second", waiting.Text);
        Assert.NotNull(waiting.Input);
        Assert.True(model.HasPendingRun);
        Assert.Empty(observer.Turns);
        var result = await gateway.ExchangeTextAsync(binding, reply);

        Assert.Equal(ExternalConversationStatus.Completed, result.Status);
        Assert.Equal("Clarified answer", result.Text);
        Assert.Equal("Separate desktop draft", model.InputText);
        Assert.False(model.HasPendingRun);
        var run = await host.Runs.LoadAsync(waiting.Input.RunId, default);
        Assert.Equal(AgentRunStatus.Completed, run!.Snapshot.Status);
        var answer = Assert.Single(Assert.Single(run.ResolvedInputs).Reply.Answers);
        if (selectedOption) { Assert.Equal(new[] { 1 }, answer.SelectedOptionIndices); Assert.Null(answer.Text); }
        else { Assert.Empty(answer.SelectedOptionIndices); Assert.Equal(reply, answer.Text); }
        Assert.Single(observer.Turns);

        var replay = await gateway.AnswerAsync(binding, new(waiting.Input.Reference, [new("choice", [0])]));
        Assert.Equal(ExternalConversationStatus.InvalidInput, replay.Status);
        Assert.Equal("CHAT_EXTERNAL_INPUT_STALE", replay.ErrorCode);
    });

    [Fact]
    public Task External_text_does_not_answer_or_cancel_a_desktop_owned_question() => RunInputChatTest(async () =>
    {
        var provider = new ScriptedProvider(ExternalQuestionTurn());
        await using var host = await TestHost.CreateAsync(provider, new RecordingTool(ToolEffect.ReadOnly), new RecordingObserver(),
            TestSettingsStore.Create(32768), questions: true);
        using var model = new ConversationViewModel(host.Orchestrator, host.Settings);
        var binding = BindExternalConversation(host, model);
        var gateway = new ExternalConversationGateway(model, Dispatcher.CurrentDispatcher);
        model.InputText = "Desktop clarification request";
        await model.SendCommand.ExecuteAsync(null);
        var input = model.PendingInput!;
        var reference = new ExternalInputReference(input.Binding.Identity.RunId, input.Binding.RequestId, input.Binding.WaitingRevision);

        var text = await gateway.ExchangeTextAsync(binding, "2");
        var answer = await gateway.AnswerAsync(binding, new(reference, [new("choice", [1])]));
        var cancel = await gateway.CancelAsync(binding, reference);

        Assert.Equal(ExternalConversationStatus.Busy, text.Status);
        Assert.Equal("CHAT_EXTERNAL_WAITING_INPUT", text.ErrorCode);
        Assert.Equal(ExternalConversationStatus.InvalidInput, answer.Status);
        Assert.Equal(ExternalConversationStatus.InvalidInput, cancel.Status);
        Assert.True(model.HasPendingRun);
        Assert.Single(provider.Requests);
        Assert.Empty((await host.Runs.LoadAsync(reference.RunId, default))!.ResolvedInputs);
    });

    [Fact]
    public Task External_cancellation_during_answer_closes_its_original_run_without_publishing_a_final() => RunInputChatTest(async () =>
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finalRequested = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ScriptedProvider(ExternalQuestionTurn(), ProviderTurn.BlockedFinal("Cancelled clarification answer", gate));
        provider.RequestReceived = request => { if (request.IsAgentRequest && request.Tools is null) finalRequested.TrySetResult(true); };
        await using var host = await TestHost.CreateAsync(provider, new RecordingTool(ToolEffect.ReadOnly), new RecordingObserver(),
            TestSettingsStore.Create(32768), questions: true);
        using var model = new ConversationViewModel(host.Orchestrator, host.Settings);
        var binding = BindExternalConversation(host, model);
        var gateway = new ExternalConversationGateway(model, Dispatcher.CurrentDispatcher);
        var waiting = await gateway.ExchangeTextAsync(binding, "Ask before cancellation");
        Assert.Equal(ExternalConversationStatus.WaitingInput, waiting.Status);
        using var cancellation = new CancellationTokenSource();
        model.InputText = "Desktop draft during clarification";
        var reply = gateway.ExchangeTextAsync(binding, "2", cancellation.Token);
        try
        {
            await finalRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
        }
        finally { gate.TrySetResult(true); }

        Assert.Equal(ExternalConversationStatus.Cancelled, (await reply).Status);
        Assert.False(model.IsStreaming);
        Assert.False(model.IsInputSubmitting);
        Assert.False(model.HasPendingRun);
        Assert.Equal("Desktop draft during clarification", model.InputText);
        Assert.Equal(AgentRunStatus.Cancelled, (await host.Runs.LoadAsync(waiting.Input!.RunId, default))!.Snapshot.Status);
        Assert.DoesNotContain(host.Conversations.LoadMessages(binding.ConversationId, RoleId),
            message => message.Role == ChatMessageRole.Assistant && message.Status == ChatMessageStatus.Completed);
    });

    [Fact]
    public Task External_invalid_selection_and_prior_question_revision_never_dispatch() => RunInputChatTest(async () =>
    {
        var provider = new ScriptedProvider(ExternalQuestionTurn(), ExternalQuestionTurn("second-question"), ProviderTurn.Final("Done"));
        await using var host = await TestHost.CreateAsync(provider, new RecordingTool(ToolEffect.ReadOnly), new RecordingObserver(),
            TestSettingsStore.Create(32768), questions: true);
        using var model = new ConversationViewModel(host.Orchestrator, host.Settings);
        var binding = BindExternalConversation(host, model);
        var gateway = new ExternalConversationGateway(model, Dispatcher.CurrentDispatcher);

        var first = await gateway.ExchangeTextAsync(binding, "Ask two consecutive questions");
        var invalid = await gateway.ExchangeTextAsync(binding, "3");
        Assert.Equal(ExternalConversationStatus.InvalidInput, invalid.Status);
        Assert.Equal("CHAT_EXTERNAL_INVALID_ANSWER", invalid.ErrorCode);
        Assert.Single(provider.Requests);
        var second = await gateway.ExchangeTextAsync(binding, "2");
        Assert.Equal(ExternalConversationStatus.WaitingInput, second.Status);
        Assert.Equal(first.Input!.RunId, second.Input!.RunId);
        Assert.NotEqual(first.Input.Reference, second.Input.Reference);
        var beforeReplay = provider.Requests.Count;
        var stale = await gateway.AnswerAsync(binding, new(first.Input.Reference, [new("choice", [0])]));
        Assert.Equal("CHAT_EXTERNAL_INPUT_STALE", stale.ErrorCode);
        Assert.Equal(beforeReplay, provider.Requests.Count);

        Assert.Equal(ExternalConversationStatus.Completed, (await gateway.ExchangeTextAsync(binding, "1")).Status);
    });

    [Fact]
    public Task External_multiple_questions_require_desktop_or_explicit_cancel_without_guessing() => RunInputChatTest(async () =>
    {
        var provider = new ScriptedProvider(QuestionTurn());
        await using var host = await TestHost.CreateAsync(provider, new RecordingTool(ToolEffect.ReadOnly), new RecordingObserver(),
            TestSettingsStore.Create(32768), questions: true);
        using var model = new ConversationViewModel(host.Orchestrator, host.Settings);
        var binding = BindExternalConversation(host, model);
        var gateway = new ExternalConversationGateway(model, Dispatcher.CurrentDispatcher);

        var waiting = await gateway.ExchangeTextAsync(binding, "Ask multiple questions");
        Assert.Equal("CHAT_EXTERNAL_DESKTOP_INPUT_REQUIRED", waiting.ErrorCode);
        Assert.Equal(3, waiting.Input!.Questions.Count);
        var cannotGuess = await gateway.ExchangeTextAsync(binding, "2");
        Assert.Equal(ExternalConversationStatus.WaitingInput, cannotGuess.Status);
        Assert.Equal("CHAT_EXTERNAL_DESKTOP_INPUT_REQUIRED", cannotGuess.ErrorCode);
        Assert.Single(provider.Requests);
        var cancelled = await gateway.ExchangeTextAsync(binding, "/取消");

        Assert.Equal(ExternalConversationStatus.Cancelled, cancelled.Status);
        Assert.False(model.HasPendingRun);
        Assert.Single(provider.Requests);
        var run = await host.Runs.LoadAsync(waiting.Input.RunId, default);
        Assert.Equal(AgentRunStatus.Cancelled, run!.Snapshot.Status);
        Assert.Empty(run.ResolvedInputs);
    });

    private static ExternalConversationBinding BindExternalConversation(TestHost host, ConversationViewModel model)
    {
        const string conversationId = "external-conversation";
        var context = new ContentContextKey(RoleId, "test-package", "1.0.0", "default", "persona-v1", "knowledge-v1");
        host.Conversations.CreateConversation(conversationId, RoleId, context, DateTimeOffset.UtcNow, ProjectId);
        model.SetActiveServant(RoleId);
        model.SetActiveConversation(conversationId);
        Assert.Equal(conversationId, model.CurrentConversationId);
        Assert.Equal(ProjectId, model.SessionContext.ProjectId);
        return new(RoleId, conversationId, ProjectId);
    }

    private static ProviderTurn ExternalQuestionTurn(string callId = "external-question") => ProviderTurn.Call("user.ask", callId,
        """{"questions":[{"id":"choice","question":"Choose a synthetic option","options":["First","Second"]}]}""");
}
