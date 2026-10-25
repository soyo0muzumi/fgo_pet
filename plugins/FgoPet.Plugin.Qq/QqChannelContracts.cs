namespace FgoPet.Plugin.Qq;

public sealed record QqConversationBinding(string RoleId, string ConversationId, string? ProjectId);
public sealed record QqConversationReply(string? Text, string? ErrorCode = null);
public sealed record QqChannelSnapshot(string AppId, bool HasSecret, bool Enabled, bool IsPaired,
    string State, string? ErrorCode, QqConversationBinding? Binding, string? PairingCode,
    DateTimeOffset? PairingExpiresAt);
public sealed record QqDiagnostic(DateTimeOffset Timestamp, string Correlation, string Stage, string? ErrorCode = null);

internal sealed record QqReceipt(string IdHash, DateTimeOffset AcceptedAt);
internal sealed record QqStoredState(int Version = 1, string AppId = "", bool Enabled = false,
    string? OwnerOpenId = null, QqConversationBinding? Binding = null, QqReceipt[]? Receipts = null);

internal static class QqValidation
{
    internal static bool AppId(string value) => value.Length is > 0 and <= 32 && value.All(c => c is >= '0' and <= '9');
    internal static bool Identifier(string? value) => value is { Length: > 0 and <= 256 } &&
        !value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c));
    internal static bool Binding(QqConversationBinding? value) => value is not null &&
        Identifier(value.RoleId) && Identifier(value.ConversationId) &&
        (value.ProjectId is null || Identifier(value.ProjectId));
}
