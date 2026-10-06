using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Extensibility;
using FgoPet.Kernel.Agent;
using Xunit;

namespace FgoPet.Agent.Tests.Context;

public sealed class ProtectedConversationQueryResolverTests
{
    [Fact]
    public async Task Resolve_returns_original_text_after_validating_source_and_current_conversation()
    {
        var harness = Harness.Create();

        var result = await harness.Resolver.ResolveAsync(harness.Scope, harness.Reference, CancellationToken.None);

        Assert.Equal(harness.Message.Text, result);
        Assert.Equal(harness.Scope, harness.Sources.ReadScope);
        Assert.Equal(harness.Reference.UserMessageId, harness.Sources.ReadMessageId);
        Assert.Equal(ConversationSourceKind.User, harness.Sources.ReadKind);
        Assert.Same(harness.Source, harness.Sources.IsCurrentSource);
        Assert.Equal(harness.Scope.ConversationId, harness.Reader.LastConversationId);
        Assert.Equal(harness.Scope.RoleId, harness.Reader.LastServantId);
        Assert.True(harness.Timeline.IndexOf("load-messages") < harness.Timeline.IndexOf("is-current"));
        Assert.Equal(2, harness.Reader.GetConversationCount);
    }

    [Fact]
    public async Task Resolve_returns_null_for_a_malicious_reader_that_returns_another_conversation()
    {
        var harness = Harness.Create();
        harness.Reader.Conversation = Conversation("other-conversation", "role-a", "project-a");

        var result = await harness.Resolver.ResolveAsync(harness.Scope, harness.Reference, CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(0, harness.Sources.ReadCount);
    }

    [Fact]
    public async Task Resolve_rejects_wrong_message_role_owner_and_project()
    {
        var roleMismatch = Harness.Create();
        roleMismatch.Reader.Messages = [Message(roleMismatch.Scope, roleMismatch.Message.MessageId,
            ChatMessageRole.User, ChatMessageStatus.Completed, servantId: "role-b")];

        var wrongRole = await roleMismatch.Resolver.ResolveAsync(roleMismatch.Scope, roleMismatch.Reference,
            CancellationToken.None);

        Assert.Null(wrongRole);

        var projectMismatch = Harness.Create();
        projectMismatch.Reader.Conversation = Conversation("conversation-1", "role-a", "project-b");

        var wrongProject = await projectMismatch.Resolver.ResolveAsync(projectMismatch.Scope,
            projectMismatch.Reference, CancellationToken.None);

        Assert.Null(wrongProject);
    }

    [Fact]
    public async Task Resolve_rejects_changed_message_text_even_if_the_reader_returns_the_same_id()
    {
        var harness = Harness.Create();
        harness.Reader.Messages = [Message(harness.Scope, harness.Message.MessageId,
            ChatMessageRole.User, ChatMessageStatus.Completed, text: "changed after validation")];

        var result = await harness.Resolver.ResolveAsync(harness.Scope, harness.Reference, CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(0, harness.Sources.IsCurrentCallCount);
    }

    [Theory]
    [InlineData(ChatMessageStatus.Pending, ChatMessageRole.User)]
    [InlineData(ChatMessageStatus.Completed, ChatMessageRole.Assistant)]
    [InlineData(ChatMessageStatus.Cancelled, ChatMessageRole.User)]
    public async Task Resolve_rejects_messages_that_are_not_completed_user_messages(
        ChatMessageStatus status, ChatMessageRole role)
    {
        var harness = Harness.Create();
        harness.Reader.Messages = [Message(harness.Scope, harness.Message.MessageId, role, status)];

        var result = await harness.Resolver.ResolveAsync(harness.Scope, harness.Reference, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task Resolve_rejects_invalid_or_stale_source_snapshots()
    {
        var invalid = Harness.Create();
        invalid.Sources.Snapshot = invalid.Source with { Fingerprint = new string('0', 64) };

        var invalidResult = await invalid.Resolver.ResolveAsync(invalid.Scope, invalid.Reference, CancellationToken.None);

        Assert.Null(invalidResult);
        Assert.Equal(0, invalid.Reader.LoadMessagesCallCount);

        var stale = Harness.Create();
        stale.Sources.IsCurrentResult = false;

        var staleResult = await stale.Resolver.ResolveAsync(stale.Scope, stale.Reference, CancellationToken.None);

        Assert.Null(staleResult);
        Assert.Equal(1, stale.Sources.IsCurrentCallCount);
        Assert.True(stale.Timeline.IndexOf("load-messages") < stale.Timeline.IndexOf("is-current"));
    }

    [Fact]
    public async Task Resolve_rejects_malformed_fingerprints_before_reading_private_data()
    {
        var harness = Harness.Create();
        var lowerCase = harness.Reference with { Fingerprint = harness.Reference.Fingerprint.ToLowerInvariant() };
        var shortFingerprint = harness.Reference with { Fingerprint = "ABC" };

        Assert.Null(await harness.Resolver.ResolveAsync(harness.Scope, lowerCase, CancellationToken.None));
        Assert.Null(await harness.Resolver.ResolveAsync(harness.Scope, shortFingerprint, CancellationToken.None));
        Assert.Equal(0, harness.Reader.GetConversationCount);
        Assert.Equal(0, harness.Sources.ReadCount);
    }

    [Fact]
    public async Task Resolve_returns_null_for_reader_and_source_errors_without_exposing_exceptions()
    {
        var readerFailure = Harness.Create();
        readerFailure.Reader.GetConversationFailure = new InvalidOperationException("private conversation content");

        var readerResult = await readerFailure.Resolver.ResolveAsync(readerFailure.Scope,
            readerFailure.Reference, CancellationToken.None);

        Assert.Null(readerResult);

        var sourceFailure = Harness.Create();
        sourceFailure.Sources.ReadFailure = new InvalidOperationException("private source details");

        var sourceResult = await sourceFailure.Resolver.ResolveAsync(sourceFailure.Scope,
            sourceFailure.Reference, CancellationToken.None);

        Assert.Null(sourceResult);
    }

    [Fact]
    public async Task Resolve_propagates_cancellation_before_access_and_after_source_validation()
    {
        var before = Harness.Create();
        using var alreadyCancelled = new CancellationTokenSource();
        alreadyCancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await before.Resolver.ResolveAsync(before.Scope, before.Reference, alreadyCancelled.Token));

        Assert.Equal(0, before.Reader.GetConversationCount);

        var during = Harness.Create();
        using var cancellation = new CancellationTokenSource();
        during.Sources.OnIsCurrent = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await during.Resolver.ResolveAsync(during.Scope, during.Reference, cancellation.Token));

        Assert.Equal(1, during.Sources.IsCurrentCallCount);
    }

    [Fact]
    public async Task Resolve_rechecks_that_the_conversation_remains_current_and_unarchived()
    {
        var archived = Harness.Create();
        archived.Reader.ConversationSequence =
        [
            archived.Reader.Conversation!,
            Conversation("conversation-1", "role-a", "project-a", isArchived: true)
        ];

        var result = await archived.Resolver.ResolveAsync(archived.Scope, archived.Reference, CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(2, archived.Reader.GetConversationCount);
    }

    private static Conversation Conversation(string conversationId, string roleId, string? projectId,
        bool isArchived = false)
    {
        var now = DateTimeOffset.Parse("2026-10-06T12:00:00Z");
        return new Conversation(conversationId, roleId, now, now, Context(roleId), isArchived, projectId);
    }

    private static ChatMessage Message(ToolScope scope, string messageId,
        ChatMessageRole role = ChatMessageRole.User, ChatMessageStatus status = ChatMessageStatus.Completed,
        string? text = null, string? servantId = null)
    {
        var now = DateTimeOffset.Parse("2026-10-06T12:01:00Z");
        return new ChatMessage(messageId, scope.ConversationId, servantId ?? scope.RoleId, role,
            text ?? "the original user message", status, now, Context(servantId ?? scope.RoleId), 1);
    }

    private static ContentContextKey Context(string roleId) =>
        new(roleId, "test.pack", "1.0.0", "default", "persona-1", "knowledge-1");

    private static string Fingerprint(ChatMessage message) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            message.MessageId,
            message.Role,
            message.Text,
            message.CreatedAtUtc
        }))));

    private sealed class Harness
    {
        private Harness(ToolScope scope, ChatMessage message, Conversation conversation)
        {
            Scope = scope;
            Message = message;
            Reference = new ProtectedQueryReference(message.MessageId, Fingerprint(message));
            Source = new ConversationSourceSnapshot(scope, message.MessageId, Reference.Fingerprint,
                message.CreatedAtUtc, ConversationSourceKind.User, "Test project");
            Timeline = [];
            Reader = new FakeConversationReader(conversation, [message], Timeline);
            Sources = new FakeSourceReader(Source, Timeline);
            Resolver = new ProtectedConversationQueryResolver(Reader, Sources);
        }

        public ToolScope Scope { get; }
        public ChatMessage Message { get; }
        public ProtectedQueryReference Reference { get; }
        public ConversationSourceSnapshot Source { get; }
        public List<string> Timeline { get; }
        public FakeConversationReader Reader { get; }
        public FakeSourceReader Sources { get; }
        public ProtectedConversationQueryResolver Resolver { get; }

        public static Harness Create()
        {
            var scope = new ToolScope("conversation-1", "role-a", "project-a");
            var message = Message(scope, "user-message-1");
            var conversation = Conversation(scope.ConversationId, scope.RoleId, scope.ProjectId);
            return new Harness(scope, message, conversation);
        }
    }

    private sealed class FakeConversationReader(Conversation? conversation, IReadOnlyList<ChatMessage> messages,
        List<string> timeline) : IConversationReader
    {
        public Conversation? Conversation { get; set; } = conversation;
        public IReadOnlyList<Conversation?>? ConversationSequence { get; set; }
        public IReadOnlyList<ChatMessage> Messages { get; set; } = messages;
        public Exception? GetConversationFailure { get; set; }
        public int GetConversationCount { get; private set; }
        public int LoadMessagesCallCount { get; private set; }
        public string? LastConversationId { get; private set; }
        public string? LastServantId { get; private set; }

        public Conversation? GetConversation(string conversationId, string servantId)
        {
            timeline.Add("get-conversation");
            GetConversationCount++;
            LastConversationId = conversationId;
            LastServantId = servantId;
            if (GetConversationFailure is not null) throw GetConversationFailure;
            return ConversationSequence is { Count: > 0 } sequence
                ? sequence[Math.Min(GetConversationCount - 1, sequence.Count - 1)]
                : Conversation;
        }

        public IReadOnlyList<ChatMessage> LoadMessages(string conversationId, string servantId)
        {
            timeline.Add("load-messages");
            LoadMessagesCallCount++;
            return Messages;
        }
    }

    private sealed class FakeSourceReader(ConversationSourceSnapshot source, List<string> timeline)
        : IConversationSourceReader
    {
        public ConversationSourceSnapshot? Snapshot { get; set; } = source;
        public bool IsCurrentResult { get; set; } = true;
        public Exception? ReadFailure { get; set; }
        public Action? OnIsCurrent { get; set; }
        public int ReadCount { get; private set; }
        public int IsCurrentCallCount { get; private set; }
        public ToolScope? ReadScope { get; private set; }
        public string? ReadMessageId { get; private set; }
        public ConversationSourceKind ReadKind { get; private set; }
        public ConversationSourceSnapshot? IsCurrentSource { get; private set; }

        public ConversationSourceSnapshot Read(ToolScope scope, string messageId, ConversationSourceKind kind)
        {
            timeline.Add("read-source");
            ReadCount++;
            ReadScope = scope;
            ReadMessageId = messageId;
            ReadKind = kind;
            if (ReadFailure is not null) throw ReadFailure;
            return Snapshot!;
        }

        public bool IsCurrent(ConversationSourceSnapshot candidate)
        {
            timeline.Add("is-current");
            IsCurrentCallCount++;
            IsCurrentSource = candidate;
            OnIsCurrent?.Invoke();
            return IsCurrentResult;
        }
    }
}
