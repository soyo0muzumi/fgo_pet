using System.Collections.Immutable;
using System.Text;
using FgoPet.Extensibility;

namespace FgoPet.Kernel.Agent;

public sealed record InteractionBinding(AgentRunIdentity Identity, int StepNumber, string CallId,
    string RequestId, long WaitingRevision, DateTimeOffset ExpiresAt);
public sealed record UserQuestion(string Id, string Text, ImmutableArray<string> Options, bool AllowMultiple);
public sealed record UserInputRequest(InteractionBinding Binding, ImmutableArray<UserQuestion> Questions);
public sealed record QuestionAnswer(string QuestionId, ImmutableArray<int> SelectedOptionIndices, string? Text);
public sealed record UserInputReply(string RunId, string RequestId, long ExpectedRevision,
    ImmutableArray<QuestionAnswer> Answers);
public enum ApprovalDecision { Allow, Deny }
public sealed record FrozenToolBinding(string PluginId, string PluginVersion, string ToolName,
    string ArgumentsFingerprint, string SchemaFingerprint, string? RootAuthorizationId, long AuthorizationRevision)
{
    public ToolResourceAuthorization? Resource { get; init; }
    public ToolBusinessConfirmation? BusinessConfirmation { get; init; }
}
public sealed record ApprovalRequest(InteractionBinding Binding, FrozenToolBinding Tool, string NormalizedArgumentsJson);
public sealed record ApprovalReply(string RunId, string RequestId, long ExpectedRevision, ApprovalDecision Decision);

/// <summary>These checks establish correlation, not reply provenance or one-time consumption.</summary>
internal static class InteractionValidation
{
    public static TimeSpan Ttl(TimeSpan? value)
    {
        var ttl = value ?? TimeSpan.FromMinutes(15);
        if (ttl < TimeSpan.FromSeconds(1) || ttl > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(value));
        return ttl;
    }

    public static InteractionBinding CreateBinding(AgentRunIdentity identity, int stepNumber, string callId,
        long waitingRevision, TimeProvider time, TimeSpan ttl)
    {
        if (identity is null || identity.Scope is null || !BoundedId(identity.RunId, 128)
            || !BoundedId(identity.RootUserMessageId, 128) || !BoundedId(identity.ModelRevision, 128)
            || !BoundedId(identity.Scope.ConversationId, 128) || !BoundedId(identity.Scope.RoleId, 128)
            || identity.Scope.ProjectId is not null && !BoundedId(identity.Scope.ProjectId, 128)
            || identity.AuthorizationRevision < 0 || stepNumber < 1 || waitingRevision < 0 || !BoundedId(callId, 128))
            throw new AgentStateException("RUN_INVALID_INTERACTION");
        return new(identity, stepNumber, callId, Guid.NewGuid().ToString("N"), waitingRevision, time.GetUtcNow() + ttl);
    }

    public static void Match(InteractionBinding binding, string runId, string requestId, long revision,
        ToolScope scope, TimeProvider time)
    {
        if (binding is null || binding.Identity is null || binding.Identity.Scope != scope
            || binding.Identity.RunId != runId || binding.RequestId != requestId || binding.WaitingRevision != revision)
            throw new AgentStateException("RUN_INTERACTION_STALE");
        if (time.GetUtcNow() >= binding.ExpiresAt) throw new AgentStateException("RUN_INTERACTION_EXPIRED");
    }

    public static bool BoundedId(string? value, int maximum) => !string.IsNullOrWhiteSpace(value)
        && value.Length <= maximum && !value.Any(char.IsWhiteSpace) && !value.Any(char.IsControl);

    public static bool QuestionId(string? value) => value is { Length: > 0 and <= 64 } && value[0] is >= 'a' and <= 'z'
        && value.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-');

    public static bool Text(string? value, int maximum) => !string.IsNullOrWhiteSpace(value)
        && value.Length <= maximum && ValidUnicode(value);

    public static bool ValidUnicode(string value)
    {
        for (var index = 0; index < value.Length;)
        {
            if (!Rune.TryGetRuneAt(value, index, out var rune)) return false;
            index += rune.Utf16SequenceLength;
        }
        return true;
    }
}
