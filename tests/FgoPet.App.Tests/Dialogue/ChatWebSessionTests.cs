using System.Text.Json;
using System.IO;
using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Packs;
using FgoPet.Dialogue.Contracts;
using FgoPet.Dialogue.Settings;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.Infrastructure.Packs;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Infrastructure.Providers;
using FgoPet.UiSdk;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

public sealed class ChatWebSessionTests : IDisposable
{
    [Fact]
    public async Task Image_chunks_need_current_identity_and_completion_and_cannot_send_partial_pixels()
    {
        using var conversation = TestDialogueFakes.CreateConversation();
        conversation.SetActiveServant("800100");
        var host = new RecordingChatHostActions { Presentation = new("Role", null, [], [], Actions: new(CanChooseImage: true)) };
        using var session = new ChatWebSession(conversation, host);
        var identity = session.ReadSnapshot().Identity; var transfer = Guid.NewGuid().ToString("N");
        string Chunk(int offset, bool complete) => Identity(identity).TrimEnd('}') + JsonSerializer.Serialize(new {
            transferId = transfer, mimeType = "image/png", offset, data = "AQID", complete }).Replace("{", ",");
        Assert.True((await session.HandleCommandAsync(Message("chat.image", Chunk(0, false)), default)).Success);
        Assert.Empty(host.Imports);
        Assert.Equal("CHAT_IMAGE_BUSY", (await session.HandleCommandAsync(Message("chat.send", Identity(identity)), default)).ErrorCode);
        Assert.True((await session.HandleCommandAsync(Message("chat.image", Chunk(3, true)), default)).Success);
        Assert.Equal(new byte[] {1,2,3,1,2,3}, Assert.Single(host.Imports));
        Assert.True((await session.HandleCommandAsync(Message("chat.image", Chunk(0, false)), default)).Success);
        conversation.NewConversationCommand.Execute(null);
        Assert.Equal("CHAT_IDENTITY_STALE", (await session.HandleCommandAsync(Message("chat.image", Chunk(3, true)), default)).ErrorCode);
        Assert.Single(host.Imports);
    }

    [Theory]
    [InlineData("\"offset\":\"0\"")]
    [InlineData("\"offset\":-1")]
    [InlineData("\"offset\":8388609")]
    [InlineData("\"offset\":0,\"path\":\"arbitrary\"")]
    public async Task Image_bridge_rejects_malformed_or_path_bearing_payload(string fields)
    {
        using var conversation = TestDialogueFakes.CreateConversation();
        using var session = new ChatWebSession(conversation, new RecordingChatHostActions());
        var payload = Identity(session.ReadSnapshot().Identity).TrimEnd('}') + ",\"transferId\":\"" + Guid.NewGuid().ToString("N")
            + "\",\"mimeType\":\"image/png\",\"data\":\"AQID\",\"complete\":true," + fields + "}";
        Assert.Equal("CHAT_COMMAND_INVALID", (await session.HandleCommandAsync(Message("chat.image", payload), default)).ErrorCode);
    }

    [Theory]
    [InlineData("chat.approval.allow", "")]
    [InlineData("chat.approval.review", ",\"decision\":\"Allow\"")]
    [InlineData("chat.approval.review", ",\"allow\":true")]
    [InlineData("chat.approval.review", ",\"arguments\":{}")]
    [InlineData("chat.approval.review", ",\"scope\":\"other\"")]
    [InlineData("chat.approval.review", ",\"requestId\":\"duplicate\"")]
    public async Task Approval_bridge_never_accepts_page_allow_decision_or_mutable_arguments(string type, string extra)
    {
        using var model = TestDialogueFakes.CreateConversation();
        using var session = new ChatWebSession(model, new RecordingChatHostActions());
        var payload = Identity(session.ReadSnapshot().Identity).TrimEnd('}')
            + ",\"runId\":\"run\",\"requestId\":\"request\",\"revision\":1" + extra + "}";
        var result = await session.HandleCommandAsync(Message(type, payload), default);
        Assert.False(result.Success); Assert.Equal("CHAT_COMMAND_INVALID", result.ErrorCode);
    }

    [Fact]
    public async Task User_input_bridge_rejects_escaped_answer_over_64kib()
    {
        using var model = TestDialogueFakes.CreateConversation();
        using var session = new ChatWebSession(model, new RecordingChatHostActions());
        var identity = session.ReadSnapshot().Identity;
        var payload = JsonSerializer.SerializeToElement(new
        {
            sessionId = identity.SessionId, servantId = identity.ServantId, conversationId = identity.ConversationId,
            runId = "run", requestId = "question", revision = 1,
            answers = new[] { new { questionId = "free", selectedOptionIndices = Array.Empty<int>(), text = new string('中', 11987) } },
        });
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(payload.GetRawText()) > 65536);
        var rejected = await session.HandleCommandAsync(new("chat.input.answer", "fixture", payload), default);
        Assert.False(rejected.Success);
        Assert.Equal("CHAT_COMMAND_INVALID", rejected.ErrorCode);
    }

    [Theory]
    [InlineData("\"answers\":[]")]
    [InlineData("\"answers\":{}")]
    [InlineData("\"answers\":[{\"questionId\":\"q\",\"selectedOptionIndices\":[\"0\"],\"text\":null}]")]
    [InlineData("\"answers\":[{\"questionId\":\"q\",\"selectedOptionIndices\":[2147483648],\"text\":null}]")]
    [InlineData("\"answers\":[{\"questionId\":\"q\",\"selectedOptionIndices\":[],\"text\":true}]")]
    [InlineData("\"answers\":[{\"questionId\":\"q\",\"selectedOptionIndices\":[],\"text\":\"\\ud800\"}]")]
    [InlineData("\"answers\":[{\"questionId\":\"q\",\"selectedOptionIndices\":[],\"text\":\"answer\",\"authorize\":true}]")]
    [InlineData("\"answers\":[{\"questionId\":\"q\",\"selectedOptionIndices\":[],\"text\":\"a\",\"text\":\"b\"}]")]
    [InlineData("\"answers\":[{\"questionId\":\"q\",\"selectedOptionIndices\":[],\"text\":\"answer\"}],\"scope\":\"untrusted\"")]
    public async Task User_input_bridge_rejects_malformed_closed_answer_schema(string fields)
    {
        using var model = TestDialogueFakes.CreateConversation();
        using var session = new ChatWebSession(model, new RecordingChatHostActions());
        var identity = session.ReadSnapshot().Identity;
        var payload = Identity(identity).TrimEnd('}') + ",\"runId\":\"run\",\"requestId\":\"request\",\"revision\":1," + fields + "}";
        var rejected = await session.HandleCommandAsync(Message("chat.input.answer", payload), default);
        Assert.False(rejected.Success);
        Assert.Equal("CHAT_COMMAND_INVALID", rejected.ErrorCode);
    }

    [Theory]
    [InlineData("chooseWorkspace", ChatWebHostAction.ChooseWorkspace)]
    [InlineData("clearWorkspace", ChatWebHostAction.ClearWorkspace)]
    public async Task Workspace_bridge_requires_live_identity_availability_and_has_no_path_payload(string action, ChatWebHostAction expected)
    {
        using var conversation = TestDialogueFakes.CreateConversation();
        conversation.SetActiveServant("800100");
        var host = new RecordingChatHostActions
        {
            Presentation = new("Role", null, [], [], Actions: new(CanChooseWorkspace: true, CanClearWorkspace: true), WorkspaceLabel: "Synthetic workspace"),
        };
        using var session = new ChatWebSession(conversation, host);
        var identity = session.ReadSnapshot().Identity;
        string Payload(bool injectPath = false) => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["sessionId"] = identity.SessionId, ["servantId"] = identity.ServantId,
            ["conversationId"] = identity.ConversationId, ["action"] = action,
        }.Concat(injectPath ? new Dictionary<string, object?> { ["path"] = "C:\\untrusted" } : []).ToDictionary(pair => pair.Key, pair => pair.Value));
        Assert.Equal("Synthetic workspace", session.ReadSnapshot().Presentation.WorkspaceLabel);
        Assert.False((await session.HandleCommandAsync(Message("chat.host", Payload(true)), default)).Success);
        Assert.Empty(host.Requests);
        Assert.True((await session.HandleCommandAsync(Message("chat.host", Payload()), default)).Success);
        Assert.Equal(expected, Assert.Single(host.Requests).Action);
        conversation.IsStreaming = true;
        Assert.False((await session.HandleCommandAsync(Message("chat.host", Payload()), default)).Success);
        conversation.IsStreaming = false;
        conversation.NewConversationCommand.Execute(null);
        Assert.False((await session.HandleCommandAsync(Message("chat.host", Payload()), default)).Success);
        Assert.Single(host.Requests);
    }
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"fgo-chat-web-{Guid.NewGuid():N}.db");

    [Fact]
    public void Disposed_surface_does_not_destroy_the_shared_conversation()
    {
        using var conversation = TestDialogueFakes.CreateConversation();
        conversation.SetActiveServant("800100");
        conversation.InputText = "仍在编辑";
        var host = new RecordingChatHostActions();
        var session = new ChatWebSession(conversation, host);

        session.Dispose();
        conversation.InputText = "收起后的草稿";

        Assert.Equal("收起后的草稿", conversation.InputText);
        Assert.True(conversation.SendCommand.CanExecute(null));
    }

    [Fact]
    public async Task Get_is_a_read_only_projection_and_reports_live_draft()
    {
        using var conversation = TestDialogueFakes.CreateConversation();
        conversation.SetActiveServant("800100");
        var session = new ChatWebSession(conversation, new RecordingChatHostActions());
        var identity = session.ReadSnapshot().Identity;
        var message = Message("chat.get", "{}");

        var first = session.ReadSnapshot();
        var result = await session.HandleCommandAsync(message, CancellationToken.None);
        var after = session.ReadSnapshot();

        Assert.True(result.Success);
        Assert.Equal(string.Empty, identity.ConversationId);
        Assert.Equal(identity.ConversationId, conversation.CurrentConversationId);
        Assert.Empty(conversation.History);
        Assert.Equal("", first.Draft.Text);
        Assert.True(after.Version > first.Version);
    }

    [Theory]
    [InlineData("chat.stop", "{\"sessionId\":\"s\",\"servantId\":\"800100\",\"conversationId\":\"\",\"extra\":true}")]
    [InlineData("chat.stop", "{\"sessionId\":\"s\",\"sessionId\":\"s\",\"servantId\":\"800100\",\"conversationId\":\"\"}")]
    [InlineData("chat.host", "{\"sessionId\":\"s\",\"servantId\":\"800100\",\"conversationId\":\"\",\"action\":\"unknown\"}")]
    [InlineData("chat.draft", "{\"sessionId\":\"s\",\"servantId\":\"800100\",\"conversationId\":\"\",\"text\":12,\"revision\":1}")]
    public async Task Malformed_payloads_are_rejected_without_host_side_effects(string type, string payload)
    {
        using var conversation = TestDialogueFakes.CreateConversation();
        conversation.SetActiveServant("800100");
        var host = new RecordingChatHostActions();
        using var session = new ChatWebSession(conversation, host);

        var result = await session.HandleCommandAsync(Message(type, payload), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Empty(host.Requests);
        Assert.Empty(conversation.InputText);
    }

    [Fact]
    public async Task Older_draft_revision_and_stale_servant_identity_are_rejected()
    {
        using var conversation = TestDialogueFakes.CreateConversation();
        conversation.SetActiveServant("800100");
        using var session = new ChatWebSession(conversation, new RecordingChatHostActions());
        var initial = session.ReadSnapshot().Identity;

        var newer = await session.HandleCommandAsync(Message("chat.draft", Draft(initial, "新输入", 2)), CancellationToken.None);
        var older = await session.HandleCommandAsync(Message("chat.draft", Draft(initial, "迟到输入", 1)), CancellationToken.None);
        Assert.Equal("新输入", conversation.InputText);
        conversation.SetActiveServant("other-servant");
        var stale = await session.HandleCommandAsync(Message("chat.stop", Identity(initial)), CancellationToken.None);

        Assert.True(newer.Success);
        Assert.False(older.Success);
        Assert.False(stale.Success);
        Assert.Empty(conversation.InputText);
    }

    [Fact]
    public async Task History_targets_outside_the_current_servant_projection_are_rejected()
    {
        using var conversation = TestDialogueFakes.CreateConversation();
        conversation.SetActiveServant("800100");
        using var session = new ChatWebSession(conversation, new RecordingChatHostActions());
        var identity = session.ReadSnapshot().Identity;
        var target = "another-servants-conversation";

        var open = await session.HandleCommandAsync(Message("chat.history.open", HistoryTarget(identity, target)), CancellationToken.None);
        var requestDelete = await session.HandleCommandAsync(Message("chat.history.requestDelete", HistoryTarget(identity, target)), CancellationToken.None);
        var confirmDelete = await session.HandleCommandAsync(Message("chat.history.confirmDelete", HistoryTarget(identity, target)), CancellationToken.None);

        Assert.False(open.Success);
        Assert.False(requestDelete.Success);
        Assert.False(confirmDelete.Success);
        Assert.Null(conversation.PendingHistoryDeletion);
        Assert.Empty(conversation.CurrentConversationId);
        Assert.Empty(conversation.Turns);
    }

    [Fact]
    public async Task Duplicate_history_and_turn_ids_are_not_dispatched()
    {
        using var conversation = TestDialogueFakes.CreateConversation();
        conversation.SetActiveServant("800100");
        conversation.History.Add(new ConversationHistoryItem("same-history", "一", DateTimeOffset.UnixEpoch, "正常"));
        conversation.History.Add(new ConversationHistoryItem("same-history", "二", DateTimeOffset.UnixEpoch, "正常"));
        var host = new RecordingChatHostActions();
        using var session = new ChatWebSession(conversation, host);
        var identity = session.ReadSnapshot().Identity;
        conversation.Turns.Add(new ConversationTurnViewModel("same-turn", ChatMessageRole.Assistant, "一"));
        conversation.Turns.Add(new ConversationTurnViewModel("same-turn", ChatMessageRole.Assistant, "二"));
        var snapshot = session.ReadSnapshot();

        var history = await session.HandleCommandAsync(Message("chat.history.requestDelete", HistoryTarget(identity, "same-history")), CancellationToken.None);
        var copy = await session.HandleCommandAsync(Message("chat.host", HostTarget(identity, "copyTurn", "same-turn")), CancellationToken.None);

        Assert.False(history.Success);
        Assert.False(copy.Success);
        Assert.Empty(snapshot.History.Items);
        Assert.All(snapshot.Conversation.Turns, turn => Assert.False(turn.CanCopy));
        Assert.Null(conversation.PendingHistoryDeletion);
        Assert.Empty(host.Requests);
    }

    [Fact]
    public async Task Oversized_draft_is_rejected_without_replacing_the_existing_text()
    {
        using var conversation = TestDialogueFakes.CreateConversation();
        conversation.SetActiveServant("800100");
        conversation.InputText = "保留原草稿";
        using var session = new ChatWebSession(conversation, new RecordingChatHostActions());
        var identity = session.ReadSnapshot().Identity;

        var result = await session.HandleCommandAsync(Message("chat.draft", Draft(identity, new string('a', 12_001), 1)), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("CHAT_DRAFT_TOO_LONG", result.ErrorCode);
        Assert.Equal("保留原草稿", conversation.InputText);
    }

    [Fact]
    public void Duplicate_host_choice_ids_are_removed_from_the_snapshot()
    {
        using var conversation = TestDialogueFakes.CreateConversation();
        conversation.SetActiveServant("800100");
        var host = new RecordingChatHostActions
        {
            Presentation = new ChatWebHostPresentation("角色", null,
                [new("project-a", "项目 A", true), new("project-a", "伪造项目", false)],
                [new("model-a", "模型 A", true), new("model-a", "伪造模型", false)]),
        };
        using var session = new ChatWebSession(conversation, host);

        var snapshot = session.ReadSnapshot();

        Assert.Empty(snapshot.Presentation.Projects);
        Assert.Empty(snapshot.Presentation.Models);
    }

    [Fact]
    public async Task Stop_is_dispatched_while_send_is_waiting_for_provider_completion()
    {
        var provider = new DelayedProvider();
        using var conversation = CreateConversation(provider);
        conversation.SetActiveServant("800100");
        conversation.InputText = "开始生成";
        using var session = new ChatWebSession(conversation, new RecordingChatHostActions());
        var identity = session.ReadSnapshot().Identity;

        var send = await session.HandleCommandAsync(Message("chat.send", Identity(identity)), CancellationToken.None);
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var currentIdentity = session.ReadSnapshot().Identity;
        var duplicate = await session.HandleCommandAsync(Message("chat.send", Identity(currentIdentity)), CancellationToken.None);
        var stop = await session.HandleCommandAsync(Message("chat.stop", Identity(currentIdentity)), CancellationToken.None);
        try
        {
            var canceledBeforeRelease = await provider.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(send.Success);
            Assert.False(duplicate.Success);
            Assert.True(stop.Success);
            Assert.True(canceledBeforeRelease);
            Assert.False(provider.Release.Task.IsCompleted);
        }
        finally { provider.Release.TrySetResult(true); }
    }

    private ConversationViewModel CreateConversation(DelayedProvider provider)
    {
        var database = TestRuntimeDatabase.Create(_databasePath);
        new RuntimeDatabaseMigrator(database).Migrate();
        var settings = TestDialogueSettingsStore.WithModelConnection();
        var binding = new ContentBinding(
            new ContentContextKey("800100", "test-persona", "1.0.0", "casual", "2.1.0", "3.0.0"),
            new PersonaBundle("800100", "test-persona", "1.0.0", "2.1.0", "认真陪伴用户。", []),
            [], ["servant-core", "casual"], new string('a', 64), new string('b', 64));
        var orchestrator = new ConversationOrchestrator(
            new StaticProviderResolver(provider),
            new StaticContentResolver(binding),
            new SqliteConversationRepository(database),
            new PromptComposer(),
            TimeProvider.System,
            settings);
        return new ConversationViewModel(orchestrator, settings);
    }

    private static WebSurfaceMessage Message(string type, string payload)
    {
        using var document = JsonDocument.Parse(payload);
        return new WebSurfaceMessage(type, Guid.NewGuid().ToString("N"), document.RootElement.Clone());
    }

    private static string Identity(ChatWebIdentity identity) => JsonSerializer.Serialize(new
    {
        sessionId = identity.SessionId,
        servantId = identity.ServantId,
        conversationId = identity.ConversationId,
    });

    private static string Draft(ChatWebIdentity identity, string text, long revision) => JsonSerializer.Serialize(new
    {
        sessionId = identity.SessionId,
        servantId = identity.ServantId,
        conversationId = identity.ConversationId,
        text,
        revision,
    });

    private static string HistoryTarget(ChatWebIdentity identity, string targetConversationId) => JsonSerializer.Serialize(new
    {
        sessionId = identity.SessionId,
        servantId = identity.ServantId,
        conversationId = identity.ConversationId,
        targetConversationId,
    });

    private static string HostTarget(ChatWebIdentity identity, string action, string targetId) => JsonSerializer.Serialize(new
    {
        sessionId = identity.SessionId,
        servantId = identity.ServantId,
        conversationId = identity.ConversationId,
        action,
        targetId,
    });

    public void Dispose() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

    private sealed class RecordingChatHostActions : IChatWebHostActions
    {
        public List<byte[]> Imports { get; } = [];
        public ValueTask<WebSurfaceCommandResult> ImportImageAsync(byte[] bytes, string mimeType, CancellationToken token)
        { Imports.Add(bytes); return ValueTask.FromResult(new WebSurfaceCommandResult(true)); }
        public List<ChatWebHostRequest> Requests { get; } = [];
        public ChatWebHostPresentation Presentation { get; set; } = new("角色", null, [], []);
        public event EventHandler? Changed { add { } remove { } }
        public ChatWebHostPresentation ReadPresentation() => Presentation;
        public ValueTask<WebSurfaceCommandResult> HandleAsync(ChatWebHostRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return ValueTask.FromResult(new WebSurfaceCommandResult(true));
        }
    }

    private sealed class DelayedProvider : IChatProvider
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string ProviderId => "test";
        public string ModelId => "test-model";
        public Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ProviderModel>>([]);

        public async IAsyncEnumerable<ChatStreamChunk> StreamAsync(
            ChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            using var registration = cancellationToken.Register(() => Canceled.TrySetResult(true));
            Started.TrySetResult(true);
            await Release.Task;
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ChatStreamChunk("迟到回复", IsComplete: true);
        }
    }

    private sealed class StaticProviderResolver(IChatProvider provider) : IChatProviderResolver
    {
        public IChatProvider Resolve() => provider;
    }

    private sealed class StaticContentResolver(ContentBinding binding) : IConversationContentResolver
    {
        public Task<ContentBinding> ResolveAsync(string servantId, CancellationToken cancellationToken) => Task.FromResult(binding);
    }
}
