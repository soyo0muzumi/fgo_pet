using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using FgoPet.Core.Dialogue;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Persistence;

namespace FgoPet.Infrastructure.Dialogue;

/// <summary>Original-message lookup owned by Dialogue; it never loads a whole conversation or invokes a provider.</summary>
public sealed class SqliteExperienceSourceReader(RuntimeDatabase database) : IExperienceSourceReader
{
    private const int MaxTextUnits = 12_000;
    private const int MaxTextBytes = MaxTextUnits * 3;

    public ExperienceSourceRead Read(ConversationScope scope, HistoryAnchor anchor, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (scope is null || anchor is null || anchor.Sequence < 1 || !ValidId(anchor.ConversationId) || !ValidId(anchor.MessageId))
            return new(ExperienceSourceStatus.ScopeDenied);
        try
        {
            using var connection = database.Open();
            using var transaction = connection.BeginTransaction(deferred: true);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.Parameters.AddWithValue("$conversation", anchor.ConversationId);
            command.Parameters.AddWithValue("$servant", scope.ServantId);
            command.CommandText = "SELECT project_id FROM conversations WHERE conversation_id=$conversation AND servant_id=$servant";
            var project = command.ExecuteScalar();
            cancellationToken.ThrowIfCancellationRequested();
            if (project is null) return new(ExperienceSourceStatus.Deleted);
            if (!string.Equals(project is DBNull ? null : (string)project, scope.ProjectId, StringComparison.Ordinal))
                return new(ExperienceSourceStatus.ScopeDenied);

            command.Parameters.AddWithValue("$message", anchor.MessageId);
            command.Parameters.AddWithValue("$maxTextBytes", MaxTextBytes);
            // SQLite text slicing stops at NUL. Preserve a whole legal original (at most
            // three UTF-8 bytes per UTF-16 unit), or reject it without materializing an oversized value.
            command.CommandText = """
                SELECT sequence,created_at_utc,role,
                  CASE WHEN length(CAST(text AS BLOB))<=$maxTextBytes THEN text ELSE NULL END,status FROM chat_messages
                WHERE conversation_id=$conversation AND servant_id=$servant AND message_id=$message
                """;
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return new(ExperienceSourceStatus.Deleted);
            cancellationToken.ThrowIfCancellationRequested();
            var occurredAt = DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture);
            var role = reader.GetString(2) switch
            {
                "user" => ChatMessageRole.User,
                "assistant" => ChatMessageRole.Assistant,
                _ => (ChatMessageRole)(-1)
            };
            if (reader.GetInt32(0) != anchor.Sequence || occurredAt != anchor.CreatedAtUtc
                || role is not (ChatMessageRole.User or ChatMessageRole.Assistant) || reader.GetString(4) != "completed")
                return new(ExperienceSourceStatus.Changed);
            if (reader.IsDBNull(3)) return new(ExperienceSourceStatus.Changed);
            var text = reader.GetString(3);
            if (text.Length is < 1 or > MaxTextUnits) return new(ExperienceSourceStatus.Changed);
            // Match ConversationSourceReader's authoritative original-message fingerprint convention.
            var version = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
            {
                MessageId = anchor.MessageId, Role = role, Text = text, CreatedAtUtc = occurredAt
            })));
            cancellationToken.ThrowIfCancellationRequested();
            return new(ExperienceSourceStatus.Available, new(scope, anchor,
                role == ChatMessageRole.User ? ConversationSourceKind.User : ConversationSourceKind.Assistant, text, version));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            // SQLite exceptions can contain private data; only a typed, safe status crosses the owner port.
            cancellationToken.ThrowIfCancellationRequested();
            return new(ExperienceSourceStatus.Unavailable);
        }
    }

    private static bool ValidId(string? value) => value is { Length: > 0 and <= 128 }
        && !value.Any(char.IsWhiteSpace) && !value.Any(char.IsControl);
}
