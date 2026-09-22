using System.IO;
using FgoPet.App.Dialogue;
using FgoPet.App.Services;
using FgoPet.App.Speech;
using FgoPet.App.Settings;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Memory;
using FgoPet.Core.Packs;
using FgoPet.Core.Portraits;
using FgoPet.Core.Settings;
using FgoPet.Core.Speech;
using FgoPet.Core.Todo;
using FgoPet.Dialogue.Settings;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.Infrastructure.Memory;
using FgoPet.Infrastructure.Packs;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Infrastructure.Providers;
using FgoPet.Memory.Settings;
using FgoPet.Speech.Settings;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

public sealed class ConversationOrchestratorTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"fgo-phase3-orchestrator-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task Validated_emotion_is_published_only_with_the_completed_reply()
    {
        var provider = new FakeProvider([new ChatStreamChunk("{\"text\":\"很好！\",\"emotion\":\"happy\"}", IsComplete: true)]);
        var orchestrator = CreateOrchestrator(provider);
        var updates = new List<ConversationUpdate>();
        orchestrator.Updated += updates.Add;
        await orchestrator.SendAsync("800100", "你好", CancellationToken.None);
        var completed = Assert.Single(updates.Where(u => u.Type == ConversationUpdateType.AssistantCompleted));
        Assert.Equal(ExpressionSemantic.Happy, completed.Expression);
        Assert.DoesNotContain(updates, u => u.Type != ConversationUpdateType.AssistantCompleted && u.Expression is not null);
    }
    [Fact]
    public async Task Send_persists_user_and_final_assistant_messages_with_context()
    {
        var provider = new FakeProvider([new ChatStreamChunk("已收到"), new ChatStreamChunk("，御主", IsComplete: true)]);
        var orchestrator = CreateOrchestrator(provider);

        var result = await orchestrator.SendAsync("800100", "请陪我工作", CancellationToken.None);

        Assert.Equal(ConversationSendStatus.Completed, result.Status);
        var messages = CreateConversationRepository().LoadMessages(result.ConversationId, "800100");
        Assert.Equal(2, messages.Count);
        Assert.Equal(ChatMessageRole.User, messages[0].Role);
        Assert.Equal(ChatMessageStatus.Completed, messages[1].Status);
        Assert.Equal("已收到，御主", messages[1].Text);
        Assert.Equal("casual", messages[1].ContentContext.AppearanceId);
    }

    [Fact]
    public async Task Send_includes_bound_persona_in_the_provider_request()
    {
        var provider = new FakeProvider([new ChatStreamChunk("已收到", IsComplete: true)]);
        var orchestrator = CreateOrchestrator(provider);

        await orchestrator.SendAsync("800100", "请陪我工作", CancellationToken.None);

        Assert.NotNull(provider.LastRequest);
        Assert.Contains(provider.LastRequest!.Messages, message =>
            message.Role == ChatMessageRole.System
            && message.Text.Contains("认真陪伴用户。", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cancel_does_not_persist_partial_assistant_text()
    {
        var provider = new BlockingProvider();
        var orchestrator = CreateOrchestrator(provider);
        using var cancellation = new CancellationTokenSource();
        var task = orchestrator.SendAsync("800100", "开始", cancellation.Token);
        await provider.Started.Task;
        cancellation.Cancel();

        var result = await task;

        Assert.Equal(ConversationSendStatus.Cancelled, result.Status);
        var messages = CreateConversationRepository().LoadMessages(result.ConversationId, "800100");
        Assert.DoesNotContain(messages, message => message.Role == ChatMessageRole.Assistant && message.Status == ChatMessageStatus.Completed);
    }

    [Fact]
    public async Task Structured_memory_candidate_is_saved_as_pending()
    {
        var provider = new FakeProvider([new ChatStreamChunk("{\"text\":\"记住这件事。\",\"memory_candidate\":\"用户喜欢安静工作。\"}", IsComplete: true)]);
        var orchestrator = CreateOrchestrator(provider);

        var result = await orchestrator.SendAsync("800100", "请记住", CancellationToken.None);

        Assert.Equal(ConversationSendStatus.Completed, result.Status);
        var candidates = CreateMemoryRepository().ListCandidates("800100");
        var candidate = Assert.Single(candidates);
        Assert.Equal(MemoryCandidateStatus.Pending, candidate.Status);
        Assert.Equal("用户喜欢安静工作。", candidate.Text);
    }

    [Fact]
    public async Task Disabled_memory_does_not_write_structured_candidates()
    {
        var provider = new FakeProvider([new ChatStreamChunk("{\"text\":\"记住这件事。\",\"memory_candidate\":\"用户喜欢安静工作。\"}", IsComplete: true)]);
        var orchestrator = CreateOrchestrator(provider, memorySettings: new MemorySettingsStore(enabled: false));

        var result = await orchestrator.SendAsync("800100", "请记住", CancellationToken.None);

        Assert.Equal(ConversationSendStatus.Completed, result.Status);
        Assert.Empty(CreateMemoryRepository().ListCandidates("800100"));
    }

    [Fact]
    public async Task Structured_envelope_is_buffered_and_todo_proposals_are_transient()
    {
        var provider = new FakeProvider([new ChatStreamChunk(
            "{\"text\":\"安排好了。\",\"todos\":[{\"title\":\"写回归测试\"}]}",
            IsComplete: true)]);
        var todoService = new TodoProposalService(new TodoApplicationService(new FakeTodoRepository(), TimeProvider.System));
        var orchestrator = CreateOrchestrator(provider, todoService);
        var updates = new List<ConversationUpdate>();
        orchestrator.Updated += updates.Add;

        var result = await orchestrator.SendAsync("800100", "请安排测试", CancellationToken.None);

        Assert.Equal(ConversationSendStatus.Completed, result.Status);
        var assistantDelta = Assert.Single(updates.Where(update => update.Type == ConversationUpdateType.AssistantDelta));
        Assert.Equal("安排好了。", assistantDelta.TextDelta);
        Assert.DoesNotContain("{", assistantDelta.TextDelta, StringComparison.Ordinal);
        var completed = Assert.Single(updates.Where(update => update.Type == ConversationUpdateType.AssistantCompleted));
        Assert.NotNull(completed.StructuredResponse);
        var persisted = CreateConversationRepository().LoadMessages(result.ConversationId, "800100");
        Assert.Equal("安排好了。", persisted.Single(message => message.Role == ChatMessageRole.Assistant).Text);
    }

    [Fact]
    public async Task Completed_new_reply_is_auto_read_when_enabled()
    {
        var settings = new RecordingSettings();
        settings.Save(SpeechSettings.Defaults with
        {
            Connection = new SpeechConnectionSettings
            {
                Enabled = true,
                Provider = SpeechProviderKind.GptSoVits,
                AutoReadEnabled = true,
            },
        });
        var synthesizer = new RecordingSpeechSynthesizer();
        var player = new RecordingSpeechPlayer();
        using var synthesis = new SpeechSynthesisCoordinator(synthesizer);
        using var playback = new SpeechPlaybackCoordinator(synthesis, player, settings);
        var dialogueSettings = new RecordingDialogueSettings();
        var conversation = new ConversationViewModel(
            CreateOrchestrator(
                new FakeProvider([new ChatStreamChunk("朗读这句", IsComplete: true)]),
                settings: dialogueSettings),
            dialogueSettings);
        var dialogue = new DialogueWindowViewModel(conversation, speech: playback);
        dialogue.NotifyActivated();
        conversation.SetActiveServant("800100");
        conversation.InputText = "请回复";

        await conversation.SendCommand.ExecuteAsync(null);
        await player.Played.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("朗读这句", synthesizer.LastText);
    }
    [Fact]
    public async Task Auto_read_is_blocked_when_do_not_disturb_enabled()
    {
        var settings = new RecordingSettings();
        settings.Save(SpeechSettings.Defaults with
        {
            Connection = new SpeechConnectionSettings
            {
                Enabled = true,
                Provider = SpeechProviderKind.GptSoVits,
                AutoReadEnabled = true,
                DoNotDisturb = true,
            },
        });
        var synthesizer = new RecordingSpeechSynthesizer();
        using var synthesis = new SpeechSynthesisCoordinator(synthesizer);
        using var playback = new SpeechPlaybackCoordinator(synthesis, new RecordingSpeechPlayer(), settings);

        var result = await playback.PlayConfiguredAsync("不应自动朗读。", autoRead: true);

        Assert.False(result.Completed);
        Assert.Equal("免打扰已开启。", result.SafeError);
        Assert.Null(synthesizer.LastText);
    }
    [Fact]
    public async Task Manual_reading_exposes_configuration_recovery_on_the_message()
    {
        var settings = new RecordingSettings();
        using var synthesis = new SpeechSynthesisCoordinator(new RecordingSpeechSynthesizer());
        using var playback = new SpeechPlaybackCoordinator(synthesis, new RecordingSpeechPlayer(), settings);
        var viewModel = new DialogueWindowViewModel(
            new ConversationViewModel(CreateOrchestrator(new FakeProvider([]), settings: new FakeSettings()), new FakeSettings()),
            speech: playback);
        var turn = new ConversationTurnViewModel("assistant", ChatMessageRole.Assistant, "请先配置朗读。");

        var result = await viewModel.ReadAloudAsync(turn);

        Assert.NotNull(result);
        Assert.False(result!.Completed);
        Assert.True(turn.SpeechNeedsConfiguration);
        Assert.Equal("去朗读设置", turn.SpeechActionText);
        Assert.Contains("朗读设置", turn.SpeechStatusText, StringComparison.Ordinal);
    }
    [Fact]
    public async Task Conversation_view_model_exposes_bounded_turns_and_send_state()
    {
        var provider = new FakeProvider([new ChatStreamChunk("收到", IsComplete: true)]);
        var viewModel = new ConversationViewModel(CreateOrchestrator(provider), new FakeSettings());
        viewModel.SetActiveServant("800100");
        viewModel.InputText = "请陪我工作";

        Assert.True(viewModel.CanSend);
        await viewModel.SendCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsStreaming);
        Assert.Equal(2, viewModel.Turns.Count);
        Assert.Equal("MASTER / 我", viewModel.Turns[0].RoleLabel);
        Assert.Equal("收到", viewModel.Turns[1].Text);
    }

    [Fact]
    public async Task Conversation_view_model_clears_visible_turns_when_servant_changes()
    {
        var viewModel = new ConversationViewModel(
            CreateOrchestrator(new FakeProvider([new ChatStreamChunk("收到", IsComplete: true)])),
            new FakeSettings());
        viewModel.SetActiveServant("800100");
        viewModel.InputText = "你好";
        await viewModel.SendCommand.ExecuteAsync(null);

        viewModel.SetActiveServant("100001");

        Assert.Empty(viewModel.Turns);
        Assert.Equal("100001", viewModel.ActiveServantId);
    }

    [Fact]
    public async Task Tool_call_proposals_flow_through_the_tool_channel()
    {
        var arguments = "{\"todos\":[{\"title\":\"写回归测试\"}]}";
        var provider = new FakeProvider(
        [
            new ChatStreamChunk(string.Empty, ToolCallDelta: new ChatToolCallDelta(0, id: "call-1", name: "submit_todo_proposals")),
            new ChatStreamChunk(string.Empty, ToolCallDelta: new ChatToolCallDelta(0, argumentsDelta: arguments)),
            new ChatStreamChunk(string.Empty, IsComplete: true, FinishReason: "tool_calls"),
        ]);
        var todoService = new TodoProposalService(new TodoApplicationService(new FakeTodoRepository(), TimeProvider.System));
        var orchestrator = CreateOrchestrator(provider, todoService);
        var updates = new List<ConversationUpdate>();
        orchestrator.Updated += updates.Add;

        var result = await orchestrator.SendAsync("800100", "请安排测试", CancellationToken.None);

        Assert.Equal(ConversationSendStatus.Completed, result.Status);
        var completed = Assert.Single(updates.Where(update => update.Type == ConversationUpdateType.AssistantCompleted));
        Assert.Equal(TodoToolCallOutcome.ProposalsReady, completed.TodoOutcome);
        Assert.NotNull(completed.StructuredResponse);
    }

    [Fact]
    public async Task Pending_draft_reply_lists_every_proposal_and_its_step_titles_without_internal_details()
    {
        var provider = new FakeProvider(
        [
            new ChatStreamChunk(string.Empty, ToolCallDelta: new ChatToolCallDelta(0, id: "call-1", name: "submit_todo_proposals")),
            new ChatStreamChunk(string.Empty, ToolCallDelta: new ChatToolCallDelta(0, argumentsDelta: "{\"todos\":[{\"title\":\"准备发布\",\"steps\":[{\"title\":\"整理变更\"},{\"title\":\"运行检查\"}]},{\"title\":\"安排复盘\",\"steps\":[{\"title\":\"收集反馈\"}]}]}")),
            new ChatStreamChunk(string.Empty, IsComplete: true, FinishReason: "tool_calls"),
        ]);
        var repository = new RecordingTodoRepository();
        var todoService = new TodoProposalService(new TodoApplicationService(repository, TimeProvider.System));
        var orchestrator = CreateOrchestrator(provider, todoService);
        var updates = new List<ConversationUpdate>();
        orchestrator.Updated += updates.Add;

        var result = await orchestrator.SendAsync("800100", "请安排发布和复盘", CancellationToken.None);

        Assert.Equal(ConversationSendStatus.Completed, result.Status);
        var reply = Assert.Single(updates.Where(update => update.Type == ConversationUpdateType.AssistantDelta));
        Assert.Contains("准备发布", reply.TextDelta, StringComparison.Ordinal);
        Assert.Contains("整理变更", reply.TextDelta, StringComparison.Ordinal);
        Assert.Contains("运行检查", reply.TextDelta, StringComparison.Ordinal);
        Assert.Contains("安排复盘", reply.TextDelta, StringComparison.Ordinal);
        Assert.Contains("收集反馈", reply.TextDelta, StringComparison.Ordinal);
        Assert.Contains("尚未创建", reply.TextDelta, StringComparison.Ordinal);
        Assert.Contains("修改", reply.TextDelta, StringComparison.Ordinal);
        Assert.Contains("确认", reply.TextDelta, StringComparison.Ordinal);
        Assert.DoesNotContain("call-1", reply.TextDelta, StringComparison.Ordinal);
        Assert.DoesNotContain("schema", reply.TextDelta, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("submit_todo_proposals", reply.TextDelta, StringComparison.Ordinal);
        Assert.Empty(repository.Items);
    }

    [Fact]
    public async Task Confirmed_todo_id_is_attached_to_the_corresponding_assistant_turn()
    {
        var provider = new FakeProvider(
        [
            new ChatStreamChunk(string.Empty, ToolCallDelta: new ChatToolCallDelta(0, id: "call-1", name: "submit_todo_proposals")),
            new ChatStreamChunk(string.Empty, ToolCallDelta: new ChatToolCallDelta(0, argumentsDelta: "{\"todos\":[{\"title\":\"关联入口测试\"}]}")),
            new ChatStreamChunk(string.Empty, IsComplete: true, FinishReason: "tool_calls"),
        ]);
        var repository = new RecordingTodoRepository();
        var todoService = new TodoProposalService(new TodoApplicationService(repository, TimeProvider.System));
        var viewModel = new ConversationViewModel(CreateOrchestrator(provider, todoService), new FakeSettings());
        viewModel.SetActiveServant("800100");
        viewModel.InputText = "请安排入口测试";

        await viewModel.SendCommand.ExecuteAsync(null);

        var draftReply = Assert.Single(viewModel.Turns.Where(turn => turn.IsAssistant));
        Assert.False(draftReply.CanViewTodo);

        viewModel.InputText = "确认";
        await viewModel.SendCommand.ExecuteAsync(null);

        var replies = viewModel.Turns.Where(turn => turn.IsAssistant).ToArray();
        var confirmedReply = Assert.Single(replies, turn => turn.Text.Contains("已创建待办", StringComparison.Ordinal));
        var created = Assert.Single(repository.Items);
        Assert.True(confirmedReply.CanViewTodo);
        Assert.Equal(created.Id, confirmedReply.CreatedTodoId);
        Assert.DoesNotContain(replies, turn => !ReferenceEquals(turn, confirmedReply) && turn.CanViewTodo);
    }

    [Fact]
    public async Task Invalid_tool_arguments_surface_a_typed_error()
    {
        var provider = new FakeProvider(
        [
            new ChatStreamChunk(string.Empty, ToolCallDelta: new ChatToolCallDelta(0, id: "call-1", name: "submit_todo_proposals")),
            new ChatStreamChunk(string.Empty, ToolCallDelta: new ChatToolCallDelta(0, argumentsDelta: """{"todos":[{"title":"x","command":"rm -rf /"}]}""")),
            new ChatStreamChunk(string.Empty, IsComplete: true, FinishReason: "tool_calls"),
        ]);
        var todoService = new TodoProposalService(new TodoApplicationService(new FakeTodoRepository(), TimeProvider.System));
        var orchestrator = CreateOrchestrator(provider, todoService);
        var updates = new List<ConversationUpdate>();
        orchestrator.Updated += updates.Add;

        await orchestrator.SendAsync("800100", "请安排测试", CancellationToken.None);

        var completed = Assert.Single(updates.Where(update => update.Type == ConversationUpdateType.AssistantCompleted));
        Assert.Equal(TodoToolCallOutcome.InvalidToolCall, completed.TodoOutcome);
        Assert.Contains("command", completed.TodoDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Planning_turn_without_tool_call_reports_empty_state()
    {
        var provider = new FakeProvider([new ChatStreamChunk("我先想想怎么安排。", IsComplete: true)]);
        var todoService = new TodoProposalService(new TodoApplicationService(new FakeTodoRepository(), TimeProvider.System));
        var orchestrator = CreateOrchestrator(provider, todoService, settings: new FakeSettings());
        var updates = new List<ConversationUpdate>();
        orchestrator.Updated += updates.Add;

        await orchestrator.SendAsync("800100", "帮我安排今天的工作", CancellationToken.None);

        var completed = Assert.Single(updates.Where(update => update.Type == ConversationUpdateType.AssistantCompleted));
        Assert.Equal(TodoToolCallOutcome.NoProposal, completed.TodoOutcome);
        Assert.Null(completed.StructuredResponse);
    }

    [Fact]
    public async Task Length_truncation_never_produces_proposals()
    {
        var arguments = "{\"todos\":[{\"title\":\"写回归";
        var provider = new FakeProvider(
        [
            new ChatStreamChunk(string.Empty, ToolCallDelta: new ChatToolCallDelta(0, id: "call-1", name: "submit_todo_proposals")),
            new ChatStreamChunk(string.Empty, ToolCallDelta: new ChatToolCallDelta(0, argumentsDelta: arguments)),
            new ChatStreamChunk(string.Empty, IsComplete: true, FinishReason: "length"),
        ]);
        var todoService = new TodoProposalService(new TodoApplicationService(new FakeTodoRepository(), TimeProvider.System));
        var orchestrator = CreateOrchestrator(provider, todoService);
        var updates = new List<ConversationUpdate>();
        orchestrator.Updated += updates.Add;

        await orchestrator.SendAsync("800100", "请安排测试", CancellationToken.None);

        var completed = Assert.Single(updates.Where(update => update.Type == ConversationUpdateType.AssistantCompleted));
        Assert.Equal(TodoToolCallOutcome.InvalidToolCall, completed.TodoOutcome);
        Assert.Null(completed.StructuredResponse);
    }

    [Fact]
    public async Task Tools_rejection_degrades_to_text_retry_and_persists_the_downgrade()
    {
        var provider = new DegradingProvider(
        [
            new ChatStreamChunk(
                "{\"text\":\"安排好了。\",\"todos\":[{\"title\":\"写回归测试\"}]}",
                IsComplete: true),
        ]);
        var settings = new RecordingDialogueSettings();
        var todoService = new TodoProposalService(new TodoApplicationService(new FakeTodoRepository(), TimeProvider.System));
        var orchestrator = CreateOrchestrator(provider, todoService, settings: settings);
        var updates = new List<ConversationUpdate>();
        orchestrator.Updated += updates.Add;

        var result = await orchestrator.SendAsync("800100", "请安排测试", CancellationToken.None);

        Assert.Equal(ConversationSendStatus.Completed, result.Status);
        Assert.True(provider.FirstCallHadTools);
        Assert.False(provider.LastCallHadTools);
        Assert.False(settings.Saved!.ModelConnection!.ToolsSupported);
        var completed = Assert.Single(updates.Where(update => update.Type == ConversationUpdateType.AssistantCompleted));
        Assert.Equal(TodoToolCallOutcome.TextFallback, completed.TodoOutcome);
        Assert.Contains(
            "文本提案兜底",
            updates.Where(u => u.Type == ConversationUpdateType.AssistantDelta).Select(u => u.SafeError).Single(text => !string.IsNullOrEmpty(text)),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Connection_marked_unsupported_skips_tools_entirely()
    {
        var provider = new FakeProvider([new ChatStreamChunk("收到。", IsComplete: true)]);
        var settings = new RecordingDialogueSettings(supportsTools: false);
        var orchestrator = CreateOrchestrator(provider, todoProposals: null, settings: settings);

        await orchestrator.SendAsync("800100", "你好", CancellationToken.None);

        Assert.NotNull(provider.LastRequest);
        Assert.Null(provider.LastRequest!.Tools);
    }

    [Fact]
    public async Task Unexpected_model_stage_failure_reports_that_request_stage_is_the_boundary()
    {
        var orchestrator = CreateOrchestrator(new ThrowingProvider());

        var result = await orchestrator.SendAsync("800100", "你好", CancellationToken.None);

        Assert.Equal(ConversationSendStatus.Failed, result.Status);
        Assert.Equal("对话服务暂时不可用：请检查模型连接设置后重试。", result.SafeError);
    }

    [Fact]
    public async Task Unexpected_role_package_failure_reports_the_role_package_stage()
    {
        var orchestrator = CreateOrchestrator(
            new FakeProvider([]),
            contentResolver: new ThrowingContentResolver());

        var result = await orchestrator.SendAsync("800100", "你好", CancellationToken.None);

        Assert.Equal(ConversationSendStatus.Failed, result.Status);
        Assert.Equal("对话初始化失败：角色包内容不可用，请检查当前角色包后重试。", result.SafeError);
    }

    [Fact]
    public async Task Reasoning_delta_flows_to_updates_and_never_persists()
    {
        var provider = new FakeProvider(
        [
            new ChatStreamChunk(string.Empty, ReasoningDelta: "用户想要一个待办…"),
            new ChatStreamChunk("好的，安排。", IsComplete: true),
        ]);
        var orchestrator = CreateOrchestrator(provider);
        var updates = new List<ConversationUpdate>();
        orchestrator.Updated += updates.Add;

        var result = await orchestrator.SendAsync("800100", "请安排", CancellationToken.None);

        Assert.Equal(ConversationSendStatus.Completed, result.Status);
        var persisted = CreateConversationRepository().LoadMessages(result.ConversationId, "800100");
        Assert.All(persisted, message => Assert.DoesNotContain("用户想要一个待办", message.Text, StringComparison.Ordinal));
        Assert.Contains(updates, update => update.ReasoningDelta == "用户想要一个待办…");
    }

    [Fact]
    public async Task Reasoning_streams_into_the_turn_and_collapses_after_content()
    {
        var provider = new FakeProvider(
        [
            new ChatStreamChunk(string.Empty, ReasoningDelta: "先分析需求"),
            new ChatStreamChunk(string.Empty, ReasoningDelta: "，再列出步骤"),
            new ChatStreamChunk("好的。", IsComplete: true),
        ]);
        var viewModel = new ConversationViewModel(CreateOrchestrator(provider), new FakeSettings());
        viewModel.SetActiveServant("800100");
        viewModel.InputText = "请安排";

        await viewModel.SendCommand.ExecuteAsync(null);

        var assistantTurn = Assert.Single(viewModel.Turns, turn => turn.Role == ChatMessageRole.Assistant);
        Assert.Equal("先分析需求，再列出步骤", assistantTurn.ReasoningText);
        Assert.Equal("好的。", assistantTurn.Text);
        Assert.False(assistantTurn.IsThinkingActive);
        Assert.False(assistantTurn.IsReasoningExpanded);
        Assert.False(viewModel.IsThinking);
    }

    [Fact]
    public async Task Reasoning_without_text_ends_the_timer_without_leaving_a_bubble()
    {
        var provider = new FakeProvider(
        [
            new ChatStreamChunk(string.Empty, ReasoningDelta: "工具调用前的思考"),
            new ChatStreamChunk(string.Empty, ToolCallDelta: new ChatToolCallDelta(0, id: "call-1", name: "submit_todo_proposals")),
            new ChatStreamChunk(string.Empty, ToolCallDelta: new ChatToolCallDelta(0, argumentsDelta: """{"todos":[{"title":"写测试"}]}""")),
            new ChatStreamChunk(string.Empty, IsComplete: true, FinishReason: "tool_calls"),
        ]);
        var todoService = new TodoProposalService(new TodoApplicationService(new FakeTodoRepository(), TimeProvider.System));
        var viewModel = new ConversationViewModel(CreateOrchestrator(provider, todoService), new FakeSettings());
        viewModel.SetActiveServant("800100");
        viewModel.InputText = "请安排";

        await viewModel.SendCommand.ExecuteAsync(null);

        var reasoningTurn = Assert.Single(viewModel.Turns, turn => turn.ReasoningText.Length > 0);
        Assert.Equal("工具调用前的思考", reasoningTurn.ReasoningText);
        Assert.Contains("尚未创建", reasoningTurn.Text, StringComparison.Ordinal);
        Assert.False(viewModel.IsThinking);
        Assert.Equal(string.Empty, viewModel.ThinkingTimerText);
    }

    [Fact]
    public void Empty_turns_have_no_visible_text_and_reasoning_is_collapsed_by_default()
    {
        var turn = new ConversationTurnViewModel("assistant", ChatMessageRole.Assistant, string.Empty);

        Assert.False(turn.HasVisibleText);
        Assert.False(turn.IsReasoningExpanded);

        turn.AppendReasoning("只含思考");
        Assert.True(turn.HasVisibleReasoning);
        turn.Append("正文");
        Assert.True(turn.HasVisibleText);
        Assert.False(turn.IsReasoningExpanded);
    }

    [Fact]
    public void History_hides_only_the_known_assistant_tool_placeholder()
    {
        var repository = CreateConversationRepository();
        var context = new ContentContextKey("800100", "test-persona", "1.0.0", "casual", "2.1.0", "3.0.0");
        var created = DateTimeOffset.UtcNow;
        repository.CreateConversation("history-placeholder", "800100", context, created);
        repository.Append(new ChatMessage(
            "user-1", "history-placeholder", "800100", ChatMessageRole.User,
            "[工具调用：待办提案] 请保留普通文本", ChatMessageStatus.Completed, created, context, 1));
        repository.Append(new ChatMessage(
            "assistant-1", "history-placeholder", "800100", ChatMessageRole.Assistant,
            "[工具调用：待办提案]", ChatMessageStatus.Completed, created.AddSeconds(1), context, 2));

        var viewModel = new ConversationViewModel(CreateOrchestrator(new FakeProvider([])), new FakeSettings());
        viewModel.SetActiveServant("800100");
        viewModel.SetActiveConversation("history-placeholder");

        var turn = Assert.Single(viewModel.Turns);
        Assert.Equal(ChatMessageRole.User, turn.Role);
        Assert.Equal("[工具调用：待办提案] 请保留普通文本", turn.Text);
    }

    [Fact]
    public async Task Tools_are_offered_when_the_connection_supports_them()
    {
        var provider = new FakeProvider([new ChatStreamChunk("收到。", IsComplete: true)]);
        var orchestrator = CreateOrchestrator(provider, settings: new FakeSettings());

        await orchestrator.SendAsync("800100", "你好", CancellationToken.None);

        Assert.NotNull(provider.LastRequest);
        var tool = Assert.Single(provider.LastRequest!.Tools!);
        Assert.Equal(TodoToolContracts.SubmitTodoProposalsToolName, tool.Name);
        Assert.Equal("auto", provider.LastRequest.ToolChoice);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var path = _databasePath + suffix;
            DeleteWithRetry(path);
        }
    }

    private static void DeleteWithRetry(string path)
    {
        for (var attempt = 0; attempt < 5 && File.Exists(path); attempt++)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(50);
            }
        }
    }

    private ConversationOrchestrator CreateOrchestrator(
        IChatProvider provider,
        TodoProposalService? todoProposals = null,
        IDialogueSettingsStore? settings = null,
        IConversationContentResolver? contentResolver = null,
        IMemorySettingsStore? memorySettings = null)
    {
        var binding = new ContentBinding(
            new ContentContextKey("800100", "test-persona", "1.0.0", "casual", "2.1.0", "3.0.0"),
            new PersonaBundle("800100", "test-persona", "1.0.0", "2.1.0", "认真陪伴用户。", []),
            [],
            ["servant-core", "casual"],
            new string('a', 64),
            new string('b', 64));
        return new ConversationOrchestrator(
            new FakeProviderResolver(provider),
            contentResolver ?? new FakeContentResolver(binding),
            CreateConversationRepository(),
            CreateMemoryRepository(),
            new PromptComposer(),
            TimeProvider.System,
            settings: settings,
            memorySettings: memorySettings,
            todoProposals: todoProposals);
    }

    [Fact]
    public void Conversation_open_settings_requests_model_connection_section()
    {
        var viewModel = new ConversationViewModel(CreateOrchestrator(new FakeProvider([])), new FakeSettings());
        SettingsSection? requested = null;
        viewModel.SettingsRequested += section => requested = section;

        viewModel.OpenSettingsCommand.Execute(null);

        Assert.Equal(SettingsSection.ModelConnection, requested);
    }

    private SqliteConversationRepository CreateConversationRepository()
    {
        var database = new RuntimeDatabase(_databasePath);
        new RuntimeDatabaseMigrator(database).Migrate();
        return new SqliteConversationRepository(database);
    }

    private SqliteMemoryRepository CreateMemoryRepository()
    {
        var database = new RuntimeDatabase(_databasePath);
        new RuntimeDatabaseMigrator(database).Migrate();
        return new SqliteMemoryRepository(database);
    }

    private sealed class FakeProviderResolver(IChatProvider provider) : IChatProviderResolver
    {
        public IChatProvider Resolve() => provider;
    }

    private sealed class FakeContentResolver(ContentBinding binding) : IConversationContentResolver
    {
        public Task<ContentBinding> ResolveAsync(string servantId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(binding);
        }
    }

    private sealed class FakeProvider(IReadOnlyList<ChatStreamChunk> chunks) : IChatProvider
    {
        public ChatRequest? LastRequest { get; private set; }

        public string ProviderId => "test";
        public string ModelId => "test-model";

        public Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ProviderModel>>([new ProviderModel(ModelId)]);

        public async IAsyncEnumerable<ChatStreamChunk> StreamAsync(
            ChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            LastRequest = request;
            foreach (var chunk in chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return chunk;
            }
        }
    }

    private sealed class BlockingProvider : IChatProvider
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string ProviderId => "test";
        public string ModelId => "test-model";

        public Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ProviderModel>>([]);

        public async IAsyncEnumerable<ChatStreamChunk> StreamAsync(
            ChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Started.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }
    }

    private sealed class ThrowingContentResolver : IConversationContentResolver
    {
        public Task<ContentBinding> ResolveAsync(string servantId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("synthetic role package failure");
    }

    private sealed class ThrowingProvider : IChatProvider
    {
        public string ProviderId => "test";
        public string ModelId => "test-model";

        public Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ProviderModel>>([]);

        public async IAsyncEnumerable<ChatStreamChunk> StreamAsync(
            ChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            throw new InvalidOperationException("synthetic model failure");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }

    private sealed class FakeSettings : IDialogueSettingsStore
    {
        public DialogueSettings Load() => DialogueSettings.Defaults with
        {
            ModelConnection = new ModelConnectionSettings("test", "https://example.test/v1", "test-model"),
        };

        public void Save(DialogueSettings settings)
        {
        }
    }

    private sealed class RecordingDialogueSettings(bool supportsTools = true) : IDialogueSettingsStore
    {
        public DialogueSettings? Saved { get; private set; }

        public DialogueSettings Load() => Saved ?? DialogueSettings.Defaults with
        {
            ModelConnection = new ModelConnectionSettings("test", "https://example.test/v1", "test-model", toolsSupported: supportsTools),
        };

        public void Save(DialogueSettings settings) => Saved = settings;
    }

    private sealed class MemorySettingsStore(bool enabled) : IMemorySettingsStore
    {
        public MemorySettings Current { get; private set; } = new(enabled);

        public MemorySettings Load() => Current;

        public void Save(MemorySettings settings) => Current = settings;
    }

    private sealed class RecordingSettings : ISpeechSettingsStore
    {
        public SpeechSettings Current { get; private set; } = SpeechSettings.Defaults;

        public SpeechSettings Load() => Current;

        public void Save(SpeechSettings settings) => Current = settings;
    }

    private sealed class DegradingProvider(IReadOnlyList<ChatStreamChunk> fallbackChunks) : IChatProvider
    {
        public bool FirstCallHadTools { get; private set; }
        public bool LastCallHadTools { get; private set; }
        private int _calls;

        public string ProviderId => "test";
        public string ModelId => "test-model";

        public Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ProviderModel>>([]);

        public async IAsyncEnumerable<ChatStreamChunk> StreamAsync(
            ChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _calls++;
            LastCallHadTools = request.Tools is { Count: > 0 };
            if (_calls == 1)
            {
                FirstCallHadTools = LastCallHadTools;
                await Task.Yield();
                throw new ProviderRequestException(ProviderFailureCategory.ToolsRejected, "当前模型服务不支持工具调用。");
            }

            foreach (var chunk in fallbackChunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return chunk;
            }
        }
    }

    private sealed class FakeTodoRepository : ITodoRepository
    {
        public void Save(TodoItem todo) { }
        public TodoItem? Get(string id) => null;
        public IReadOnlyList<TodoItem> List(TodoStatus? status = null) => Array.Empty<TodoItem>();
        public IReadOnlyList<TodoItem> ListCompletedOn(DateOnly localDate) => Array.Empty<TodoItem>();
        public void Delete(string id) { }
        public void ClearAgentTodoData() { }
    }

    private sealed class RecordingTodoRepository : ITodoRepository
    {
        private readonly Dictionary<string, TodoItem> _items = new(StringComparer.Ordinal);
        public IReadOnlyCollection<TodoItem> Items => _items.Values;
        public void Save(TodoItem todo) => _items[todo.Id] = todo;
        public TodoItem? Get(string id) => _items.GetValueOrDefault(id);
        public IReadOnlyList<TodoItem> List(TodoStatus? status = null) =>
            _items.Values.Where(item => status is null || item.Status == status).ToArray();
        public IReadOnlyList<TodoItem> ListCompletedOn(DateOnly localDate) => Array.Empty<TodoItem>();
        public void Delete(string id) => _items.Remove(id);
        public void ClearAgentTodoData() => _items.Clear();
    }
    private sealed class RecordingSpeechSynthesizer : ISpeechSynthesizer
    {
        public SpeechProviderKind Provider => SpeechProviderKind.GptSoVits;
        public string? LastText { get; private set; }

        public Task<SpeechSynthesisResult> SynthesizeAsync(
            SpeechSynthesisRequest request,
            CancellationToken cancellationToken = default)
        {
            LastText = request.Text;
            return Task.FromResult(new SpeechSynthesisResult([
                (byte)'R', (byte)'I', (byte)'F', (byte)'F', 36, 0, 0, 0,
                (byte)'W', (byte)'A', (byte)'V', (byte)'E']));
        }
    }

    private sealed class RecordingSpeechPlayer : ISpeechAudioPlayer
    {
        public TaskCompletionSource<bool> Played { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task PlayAsync(
            SpeechSynthesisResult audio,
            SpeechPlaybackOptions options,
            CancellationToken cancellationToken = default)
        {
            Played.TrySetResult(true);
            return Task.CompletedTask;
        }

        public void Stop()
        {
        }

        public void Dispose()
        {
        }
    }
}
