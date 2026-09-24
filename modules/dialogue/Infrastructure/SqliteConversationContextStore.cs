using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FgoPet.Core.Dialogue;
using FgoPet.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace FgoPet.Infrastructure.Dialogue;

/// <summary>Single SQLite projection. Raw history is never rewritten by compaction.</summary>
public sealed class SqliteConversationContextStore(RuntimeDatabase database) : IConversationContextStore
{
    public ConversationContextSnapshot Read(ConversationScope scope, string conversationId)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        return Read(connection, transaction, scope, conversationId);
    }

    private static ConversationContextSnapshot Read(SqliteConnection connection, SqliteTransaction transaction,
        ConversationScope scope, string conversationId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$id", conversationId);
        command.Parameters.AddWithValue("$servant", scope.ServantId);
        command.Parameters.AddWithValue("$project", (object?)scope.ProjectId ?? DBNull.Value);
        command.CommandText = "SELECT context_revision FROM conversations WHERE conversation_id=$id AND servant_id=$servant AND project_id IS $project";
        if (command.ExecuteScalar() is not long revision) throw new KeyNotFoundException("Conversation scope is unavailable.");
        var raw = SqliteConversationRepository.LoadMessages(connection, transaction, conversationId, scope.ServantId);
        command.CommandText = """
            SELECT x.projection_revision,x.covered_through_sequence,x.covered_through_message_id,x.prefix_fingerprint,
              s.summary_id,s.summary_text,s.created_at_utc,s.updated_at_utc,s.covered_through_sequence,s.covered_through_message_id,s.servant_id
            FROM conversation_contexts x JOIN conversation_summaries s ON s.summary_id=x.summary_id AND s.conversation_id=x.conversation_id
            WHERE x.conversation_id=$id
            """;
        using var reader = command.ExecuteReader();
        ConversationSummary? summary = null;
        long projection = 0;
        if (reader.Read())
        {
            projection = reader.GetInt64(0);
            var coverage = reader.GetInt32(1);
            var last = raw.FirstOrDefault(message => message.Sequence == coverage && message.MessageId == reader.GetString(2));
            if (last is not null && reader.GetInt32(8) == coverage && reader.GetString(9) == last.MessageId &&
                reader.GetString(10) == scope.ServantId && reader.GetString(3) == Fingerprint(raw.Where(message => message.Sequence <= coverage)))
                summary = new(reader.GetString(4), conversationId, scope.ServantId, reader.GetString(5), coverage, last.MessageId,
                    last.ContentContext, Parse(reader.GetString(6)), Parse(reader.GetString(7)));
        }
        return new(scope, conversationId, revision, projection, summary,
            raw.Where(message => message.Sequence > (summary?.CoveredThroughSequence ?? 0)).ToArray(), Fingerprint(raw));
    }

    public bool TryCommit(CompactionCommit commit)
    {
        var source = commit.Source;
        var summary = commit.Summary;
        if (commit.InputTokensBefore <= 0 || commit.InputTokensAfter < 0 || commit.InputTokensAfter >= commit.InputTokensBefore ||
            summary.ConversationId != source.ConversationId || summary.ServantId != source.Scope.ServantId ||
            summary.SummaryText.Length > 6000 || string.IsNullOrWhiteSpace(summary.SummaryText)) return false;
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        ConversationContextSnapshot current;
        try { current = Read(connection, transaction, source.Scope, source.ConversationId); }
        catch (KeyNotFoundException) { return false; }
        if (current.ContextRevision != source.ContextRevision || current.ProjectionRevision != source.ProjectionRevision ||
            current.PrefixFingerprint != source.PrefixFingerprint || current.Summary != source.Summary) return false;
        var raw = SqliteConversationRepository.LoadMessages(connection, transaction, source.ConversationId, source.Scope.ServantId);
        var end = raw.FirstOrDefault(message => message.Sequence == summary.CoveredThroughSequence && message.MessageId == summary.CoveredThroughMessageId);
        if (end is null || end.Role != ChatMessageRole.Assistant || end.Status != ChatMessageStatus.Completed ||
            end.ContentContext != summary.ContentContext || summary.CoveredThroughSequence <= (source.Summary?.CoveredThroughSequence ?? 0) ||
            raw.Where(message => message.Sequence <= summary.CoveredThroughSequence).Any(message => message.Status != ChatMessageStatus.Completed)) return false;
        var binding = SqliteContentBindingRepository.Upsert(connection, transaction, summary.ContentContext,
            summary.ContentContext.PersonaVersion, summary.ContentContext.KnowledgeVersion, summary.UpdatedAtUtc);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO conversation_summaries(summary_id,conversation_id,servant_id,summary_text,covered_through_sequence,
              covered_through_message_id,binding_id,created_at_utc,updated_at_utc)
            VALUES($summary,$conversation,$servant,$text,$sequence,$message,$binding,$created,$updated)
            """;
        command.Parameters.AddWithValue("$summary", summary.SummaryId);
        command.Parameters.AddWithValue("$conversation", source.ConversationId);
        command.Parameters.AddWithValue("$servant", source.Scope.ServantId);
        command.Parameters.AddWithValue("$text", summary.SummaryText);
        command.Parameters.AddWithValue("$sequence", summary.CoveredThroughSequence);
        command.Parameters.AddWithValue("$message", summary.CoveredThroughMessageId);
        command.Parameters.AddWithValue("$binding", binding);
        command.Parameters.AddWithValue("$created", summary.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$updated", summary.UpdatedAtUtc.ToString("O"));
        command.ExecuteNonQuery();
        command.CommandText = """
            INSERT INTO conversation_contexts(conversation_id,summary_id,projection_revision,source_revision,covered_through_sequence,
              covered_through_message_id,prefix_fingerprint,route_key,input_tokens_before,input_tokens_after,updated_at_utc)
            VALUES($conversation,$summary,$projection,$source,$sequence,$message,$fingerprint,$route,$before,$after,$updated)
            ON CONFLICT(conversation_id) DO UPDATE SET summary_id=excluded.summary_id,projection_revision=excluded.projection_revision,
              source_revision=excluded.source_revision,covered_through_sequence=excluded.covered_through_sequence,
              covered_through_message_id=excluded.covered_through_message_id,prefix_fingerprint=excluded.prefix_fingerprint,
              route_key=excluded.route_key,input_tokens_before=excluded.input_tokens_before,input_tokens_after=excluded.input_tokens_after,
              updated_at_utc=excluded.updated_at_utc
            """;
        command.Parameters.AddWithValue("$projection", current.ProjectionRevision + 1);
        command.Parameters.AddWithValue("$source", source.ContextRevision);
        command.Parameters.AddWithValue("$fingerprint", Fingerprint(raw.Where(message => message.Sequence <= summary.CoveredThroughSequence)));
        command.Parameters.AddWithValue("$route", JsonSerializer.Serialize(commit.Route));
        command.Parameters.AddWithValue("$before", commit.InputTokensBefore);
        command.Parameters.AddWithValue("$after", commit.InputTokensAfter);
        command.ExecuteNonQuery();
        transaction.Commit();
        return true;
    }

    private static string Fingerprint(IEnumerable<ChatMessage> messages) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(messages.Select(message => new { message.MessageId, message.Sequence, message.Role, message.Status,
            message.Text, message.CreatedAtUtc, message.ContentContext })))));
    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
}
