using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FgoPet.Core.Dialogue;
using FgoPet.Extensibility;

namespace FgoPet.App.Dialogue;

/// <summary>Resolves only a host-validated reference to the current completed user message.</summary>
public sealed class ProtectedConversationQueryResolver : IAgentQueryResolver
{
    private const int MaxMessageLength = 12_000;
    private readonly IConversationReader _conversations;
    private readonly IConversationSourceReader _sources;

    public ProtectedConversationQueryResolver(IConversationReader conversations, IConversationSourceReader sources)
    {
        _conversations = conversations ?? throw new ArgumentNullException(nameof(conversations));
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));
    }

    public ValueTask<string?> ResolveAsync(ToolScope scope, ProtectedQueryReference reference,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsValidScope(scope) || reference is null || !IsValidId(reference.UserMessageId)
            || !IsUpperHexFingerprint(reference.Fingerprint))
            return ValueTask.FromResult<string?>(null);

        try
        {
            var conversation = _conversations.GetConversation(scope.ConversationId, scope.RoleId);
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrentConversation(conversation, scope)) return ValueTask.FromResult<string?>(null);

            var source = _sources.Read(scope, reference.UserMessageId, ConversationSourceKind.User);
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsMatchingSource(source, scope, reference)) return ValueTask.FromResult<string?>(null);

            var messages = _conversations.LoadMessages(scope.ConversationId, scope.RoleId);
            cancellationToken.ThrowIfCancellationRequested();
            if (messages is null) return ValueTask.FromResult<string?>(null);

            ChatMessage? target = null;
            foreach (var message in messages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (message is null || message.MessageId != reference.UserMessageId) continue;
                if (target is not null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return ValueTask.FromResult<string?>(null);
                }
                target = message;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCompletedUserMessage(target, scope, reference.UserMessageId))
                return ValueTask.FromResult<string?>(null);

            var fingerprint = Fingerprint(target!);
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(fingerprint, reference.Fingerprint, StringComparison.Ordinal)
                || !string.Equals(fingerprint, source.Fingerprint, StringComparison.Ordinal)
                || source.OccurredAtUtc != target!.CreatedAtUtc)
                return ValueTask.FromResult<string?>(null);

            cancellationToken.ThrowIfCancellationRequested();
            var sourceIsCurrent = _sources.IsCurrent(source);
            cancellationToken.ThrowIfCancellationRequested();
            if (!sourceIsCurrent) return ValueTask.FromResult<string?>(null);

            var currentConversation = _conversations.GetConversation(scope.ConversationId, scope.RoleId);
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrentConversation(currentConversation, scope)) return ValueTask.FromResult<string?>(null);

            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<string?>(target.Text);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<string?>(null);
        }
    }

    private static bool IsValidScope(ToolScope? scope) => scope is not null
        && IsValidId(scope.ConversationId) && IsValidId(scope.RoleId)
        && (scope.ProjectId is null || IsValidId(scope.ProjectId));

    private static bool IsValidId(string? value) => value is { Length: > 0 and <= 128 }
        && !value.Any(char.IsControl) && !value.Any(char.IsWhiteSpace);

    private static bool IsUpperHexFingerprint(string? value) => value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static bool IsCurrentConversation(Conversation? conversation, ToolScope scope) =>
        conversation is not null && !conversation.IsArchived
        && conversation.ConversationId == scope.ConversationId
        && conversation.ServantId == scope.RoleId
        && conversation.ProjectId == scope.ProjectId;

    private static bool IsMatchingSource(ConversationSourceSnapshot? source, ToolScope scope,
        ProtectedQueryReference reference) => source is not null
        && source.Scope == scope
        && source.MessageId == reference.UserMessageId
        && source.Kind == ConversationSourceKind.User
        && source.Fingerprint == reference.Fingerprint;

    private static bool IsCompletedUserMessage(ChatMessage? message, ToolScope scope, string messageId) =>
        message is not null && message.MessageId == messageId
        && message.ConversationId == scope.ConversationId
        && message.ServantId == scope.RoleId
        && message.Role == ChatMessageRole.User
        && message.Status == ChatMessageStatus.Completed
        && message.Text is { Length: > 0 and <= MaxMessageLength };

    private static string Fingerprint(ChatMessage message) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            message.MessageId,
            message.Role,
            message.Text,
            message.CreatedAtUtc
        }))));
}
