using System.Reflection;
using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Memory;
using FgoPet.Core.Packs;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.Infrastructure.Packs;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

public sealed class ConversationStoreBoundaryTests
{
    private static readonly ContentContextKey Key = new("mash", "pack", "1", "default", "1", "1");

    [Fact]
    public void Application_storage_dependencies_are_contracts_not_sqlite_repositories()
    {
        var constructor = Assert.Single(typeof(ConversationOrchestrator).GetConstructors());
        Assert.Equal(typeof(IConversationStore), Assert.Single(constructor.GetParameters().Where(p => p.Name == "conversations")).ParameterType);
        var field = typeof(ConversationOrchestrator).GetField("_conversations", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        Assert.Equal(typeof(IConversationStore), field!.FieldType);
        Assert.Equal(typeof(IConversationReader), Assert.Single(Assert.Single(typeof(MemoryExtractionSourceReader).GetConstructors()).GetParameters()).ParameterType);
        Assert.Contains(typeof(IConversationStore), typeof(SqliteConversationRepository).GetInterfaces());
        Assert.DoesNotContain(typeof(IConversationStore).GetMethods(), method => method.Name is "SaveSummary" or "LoadSummary" or "Open");
        Assert.Equal(2, typeof(IConversationReader).GetMethods().Length);
    }

    [Fact]
    public async Task Send_resume_and_delete_work_through_a_non_sqlite_store()
    {
        var store = new InMemoryStore();
        var first = Create(store);
        var updates = new List<ConversationUpdate>();
        first.Updated += updates.Add;
        var sent = await first.SendAsync("mash", "你好", default, new("project-a", "项目 A"));
        Assert.Equal(ConversationSendStatus.Completed, sent.Status);
        Assert.Equal(new[] { "你好", "好的" }, store.LoadMessages(sent.ConversationId, "mash").Select(m => m.Text));
        Assert.Equal(sent.ConversationId, store.ReadState("LastActiveConversationId:mash"));
        Assert.Contains(updates, update => update.Type == ConversationUpdateType.AssistantCompleted);
        Assert.Same(store, first.HistoryQuery);

        var restarted = Create(store);
        var continued = await restarted.SendAsync("mash", "继续", default, new("project-a", "项目 A"));
        Assert.Equal(ConversationSendStatus.Completed, continued.Status);
        Assert.Equal(sent.ConversationId, continued.ConversationId);
        Assert.Equal(new[] { 1, 2, 3, 4 }, store.LoadMessages(sent.ConversationId, "mash").Select(m => m.Sequence));
        Assert.Empty(restarted.ListConversations("other-servant"));
        Assert.False(restarted.TryDeleteConversation(sent.ConversationId, "other-servant"));
        Assert.True(restarted.TryDeleteConversation(sent.ConversationId, "mash"));
        Assert.Null(store.ReadState("LastActiveConversationId:mash"));
        Assert.Empty(store.LoadMessages(sent.ConversationId, "mash"));
    }

    [Fact]
    public void Memory_source_validation_needs_only_the_read_port_and_keeps_scope_role_and_fingerprint_checks()
    {
        var store = new InMemoryStore();
        store.CreateConversation("history", "mash", Key, DateTimeOffset.UnixEpoch, "project-a", "项目 A");
        store.Append(new("message", "history", "mash", ChatMessageRole.User, "喜欢安静工作",
            ChatMessageStatus.Completed, DateTimeOffset.UnixEpoch, Key, 1));
        // The reader wrapper deliberately implements no writes or SQLite-specific members.
        var reader = new MemoryExtractionSourceReader(new ReadOnlyStore(store));
        var source = reader.Read(new("mash", "project-a"), "history", "message", MemoryEvidenceKind.UserStatement);
        Assert.True(reader.IsCurrent(source));
        Assert.Throws<InvalidOperationException>(() => reader.Read(new("mash", "project-b"), "history", "message", MemoryEvidenceKind.UserStatement));
        Assert.Throws<InvalidOperationException>(() => reader.Read(new("other-servant", "project-a"), "history", "message", MemoryEvidenceKind.UserStatement));
        Assert.Throws<InvalidOperationException>(() => reader.Read(new("mash", "project-a"), "history", "message", MemoryEvidenceKind.AssistantSuggestion));
        store.Messages[0] = new("message", "history", "mash", ChatMessageRole.User, "后来明确更正了偏好",
            ChatMessageStatus.Completed, DateTimeOffset.UnixEpoch, Key, 1);
        Assert.False(reader.IsCurrent(source));
        store.DeleteConversation("history", "mash");
        Assert.False(reader.IsCurrent(source));
    }

    private static ConversationOrchestrator Create(IConversationStore store) =>
        new(new ProviderResolver(), new ContentResolver(), store, new NoMemory(), new PromptComposer(), TimeProvider.System);
    private sealed class NoMemory : IMemoryRecall
    {
        public MemoryRecallSnapshot Query(MemoryScope scope, string query, int maxItems = 8, int maxChars = 6000) => new(0, []);
    }
    private sealed class ProviderResolver : IChatProviderResolver
    {
        public IChatProvider Resolve() => new Provider();
    }
    private sealed class Provider : IChatProvider
    {
        public string ProviderId => "test";
        public string ModelId => "model";
        public Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatStreamChunk> StreamAsync(ChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return new("{\"text\":\"好的\",\"emotion\":\"neutral\"}", IsComplete: true, FinishReason: "stop");
        }
    }
    private sealed class ContentResolver : IConversationContentResolver
    {
        public Task<ContentBinding> ResolveAsync(string servantId, CancellationToken cancellationToken) => Task.FromResult(
            new ContentBinding(Key, new PersonaBundle("mash", "pack", "1", "1", "认真回应。", []), [], [], new string('a', 64), new string('b', 64)));
    }
    private sealed class ReadOnlyStore(IConversationReader inner) : IConversationReader
    {
        public Conversation? GetConversation(string conversationId, string servantId) => inner.GetConversation(conversationId, servantId);
        public IReadOnlyList<ChatMessage> LoadMessages(string conversationId, string servantId) => inner.LoadMessages(conversationId, servantId);
    }
    private sealed class InMemoryStore : IConversationStore
    {
        private readonly Dictionary<string, Conversation> _conversations = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _state = new(StringComparer.Ordinal);
        public List<ChatMessage> Messages { get; } = [];
        public Conversation? GetConversation(string conversationId, string servantId) =>
            _conversations.TryGetValue(conversationId, out var value) && value.ServantId == servantId ? value : null;
        public bool Exists(string conversationId, string servantId) => GetConversation(conversationId, servantId) is not null;
        public IReadOnlyList<Conversation> ListConversations(string servantId) => _conversations.Values.Where(c => c.ServantId == servantId).ToArray();
        public IReadOnlyList<ChatMessage> LoadMessages(string conversationId, string servantId) =>
            Exists(conversationId, servantId) ? Messages.Where(m => m.ConversationId == conversationId && m.ServantId == servantId).OrderBy(m => m.Sequence).ToArray() : [];
        public Conversation CreateConversation(string conversationId, string servantId, ContentContextKey contentContext,
            DateTimeOffset createdAtUtc, string? projectId = null, string? projectLabel = null)
        {
            if (contentContext.ServantId != servantId) throw new ArgumentException("Servant mismatch.");
            var conversation = new Conversation(conversationId, servantId, createdAtUtc, createdAtUtc, contentContext, projectId: projectId, projectLabel: projectLabel);
            _conversations.Add(conversationId, conversation);
            return conversation;
        }
        public void Append(ChatMessage message)
        {
            if (!Exists(message.ConversationId, message.ServantId) ||
                message.Sequence != LoadMessages(message.ConversationId, message.ServantId).Count + 1)
                throw new InvalidOperationException("Invalid conversation or sequence.");
            Messages.Add(message);
        }
        public bool IsCurrentSource(ConversationScope scope, HistoryHit source) =>
            GetConversation(source.Anchor.ConversationId, scope.ServantId) is { } conversation && conversation.ProjectId == scope.ProjectId &&
            LoadMessages(conversation.ConversationId, scope.ServantId).Any(m => m.MessageId == source.Anchor.MessageId &&
                m.Sequence == source.Anchor.Sequence && m.CreatedAtUtc == source.Anchor.CreatedAtUtc &&
                m.Status == ChatMessageStatus.Completed && m.Role is ChatMessageRole.User or ChatMessageRole.Assistant &&
                m.Text.Contains(source.Excerpt, StringComparison.Ordinal));
        public void DeleteConversation(string conversationId, string servantId, string? activeStateKey = null)
        {
            if (!Exists(conversationId, servantId)) return;
            _conversations.Remove(conversationId);
            Messages.RemoveAll(m => m.ConversationId == conversationId);
            if (activeStateKey is not null && ReadState(activeStateKey) == conversationId) DeleteState(activeStateKey);
        }
        public string? ReadState(string key) => _state.GetValueOrDefault(key);
        public void WriteState(string key, string value, DateTimeOffset updatedAtUtc) => _state[key] = value;
        public void DeleteState(string key) => _state.Remove(key);
        public ConversationHistoryPage ReadPage(string servantId, int pageSize = 50, ConversationHistoryCursor? before = null) =>
            Page(ListConversations(servantId), pageSize, before);
        public ConversationHistoryPage ReadPage(ConversationScope scope, int pageSize = 50, ConversationHistoryCursor? before = null) =>
            Page(ListConversations(scope.ServantId).Where(c => c.ProjectId == scope.ProjectId), pageSize, before);
        private static ConversationHistoryPage Page(IEnumerable<Conversation> conversations, int pageSize, ConversationHistoryCursor? before)
        {
            if (before is not null) throw new NotSupportedException("This test fixture does not paginate.");
            return new(conversations.Take(pageSize).Select(c => new ConversationHistoryEntry(c.ConversationId, "会话", c.UpdatedAtUtc, c.IsArchived)).ToArray(), null);
        }
    }
}
