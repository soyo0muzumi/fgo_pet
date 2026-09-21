using System.Globalization;
using FgoPet.Core.Memory;
using FgoPet.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace FgoPet.Infrastructure.Memory;

public sealed class SqliteMemoryRepository
{
    private readonly RuntimeDatabase _database;

    public SqliteMemoryRepository(RuntimeDatabase database) => _database = database;

    public void AddCandidate(MemoryCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO memory_candidates(
              candidate_id, conversation_id, source_message_id, servant_id, appearance_id,
              candidate_text, status, created_at_utc, reviewed_at_utc)
            VALUES($id, $conversation, $source_message, $servant, $appearance,
              $text, $status, $created, NULL)
            """;
        command.Parameters.AddWithValue("$id", candidate.CandidateId);
        command.Parameters.AddWithValue("$conversation", candidate.ConversationId);
        command.Parameters.AddWithValue("$source_message", (object?)candidate.SourceMessageId ?? DBNull.Value);
        command.Parameters.AddWithValue("$servant", candidate.ServantId);
        command.Parameters.AddWithValue("$appearance", (object?)candidate.AppearanceId ?? DBNull.Value);
        command.Parameters.AddWithValue("$text", candidate.Text);
        command.Parameters.AddWithValue("$status", candidate.Status.ToString().ToLowerInvariant());
        command.Parameters.AddWithValue("$created", candidate.CreatedAtUtc.ToString("O"));
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<MemoryCandidate> ListCandidates(string servantId)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT candidate_id, servant_id, conversation_id, candidate_text, created_at_utc,
                   source_message_id, appearance_id, status
            FROM memory_candidates
            WHERE servant_id=$servant
            ORDER BY created_at_utc, candidate_id
            """;
        command.Parameters.AddWithValue("$servant", servantId);
        using var reader = command.ExecuteReader();
        var candidates = new List<MemoryCandidate>();
        while (reader.Read())
        {
            candidates.Add(ReadCandidate(reader));
        }

        return candidates;
    }

    public StoredMemory? ReviewCandidate(
        string candidateId,
        string servantId,
        MemoryReviewAction action,
        string? editedText,
        DateTimeOffset reviewedAtUtc)
    {
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        var candidate = ReadCandidate(connection, transaction, candidateId, servantId);
        if (candidate is null)
        {
            return null;
        }

        var text = string.IsNullOrWhiteSpace(editedText) ? candidate.Text : editedText;
        switch (action)
        {
            case MemoryReviewAction.Approve:
                UpdateCandidate(connection, transaction, candidateId, "approved", text!, reviewedAtUtc);
                var memoryId = "memory-" + Guid.NewGuid().ToString("N");
                InsertMemory(connection, transaction, memoryId, servantId, text!, candidateId, reviewedAtUtc);
                transaction.Commit();
                return new StoredMemory(memoryId, servantId, text!, true, reviewedAtUtc, reviewedAtUtc, candidateId);
            case MemoryReviewAction.Reject:
                UpdateCandidate(connection, transaction, candidateId, "rejected", candidate.Text, reviewedAtUtc);
                transaction.Commit();
                return null;
            case MemoryReviewAction.Edit:
                if (string.IsNullOrWhiteSpace(editedText))
                {
                    throw new ArgumentException("Edited memory text is required.", nameof(editedText));
                }

                UpdateCandidate(connection, transaction, candidateId, "pending", editedText, reviewedAtUtc);
                transaction.Commit();
                return null;
            case MemoryReviewAction.Delete:
                DeleteCandidate(connection, transaction, candidateId, servantId);
                transaction.Commit();
                return null;
            default:
                throw new ArgumentOutOfRangeException(nameof(action));
        }
    }

    public IReadOnlyList<StoredMemory> ListEnabledMemories(string servantId) => ListMemories(servantId, enabledOnly: true);

    public IReadOnlyList<StoredMemory> ListMemories(string servantId) => ListMemories(servantId, enabledOnly: false);

    public void ReviewMemory(
        string memoryId,
        string servantId,
        MemoryReviewAction action,
        string? editedText,
        DateTimeOffset updatedAtUtc)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$id", memoryId);
        command.Parameters.AddWithValue("$servant", servantId);
        command.Parameters.AddWithValue("$updated", updatedAtUtc.ToString("O"));
        switch (action)
        {
            case MemoryReviewAction.Approve:
                command.CommandText = "UPDATE memories SET is_enabled=1, updated_at_utc=$updated WHERE memory_id=$id AND servant_id=$servant";
                break;
            case MemoryReviewAction.Disable:
                command.CommandText = "UPDATE memories SET is_enabled=0, updated_at_utc=$updated WHERE memory_id=$id AND servant_id=$servant";
                break;
            case MemoryReviewAction.Edit:
                if (string.IsNullOrWhiteSpace(editedText))
                {
                    throw new ArgumentException("Edited memory text is required.", nameof(editedText));
                }

                command.CommandText = "UPDATE memories SET memory_text=$text, updated_at_utc=$updated WHERE memory_id=$id AND servant_id=$servant";
                command.Parameters.AddWithValue("$text", editedText);
                break;
            case MemoryReviewAction.Delete:
                command.CommandText = "DELETE FROM memories WHERE memory_id=$id AND servant_id=$servant";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action));
        }

        command.ExecuteNonQuery();
    }

    private IReadOnlyList<StoredMemory> ListMemories(string servantId, bool enabledOnly)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT memory_id, servant_id, memory_text, is_enabled, created_at_utc, updated_at_utc, source_candidate_id
            FROM memories
            WHERE servant_id=$servant {(enabledOnly ? "AND is_enabled=1" : string.Empty)}
            ORDER BY updated_at_utc, memory_id
            """;
        command.Parameters.AddWithValue("$servant", servantId);
        using var reader = command.ExecuteReader();
        var memories = new List<StoredMemory>();
        while (reader.Read())
        {
            memories.Add(new StoredMemory(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3) == 1,
                ParseUtc(reader.GetString(4)),
                ParseUtc(reader.GetString(5)),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        return memories;
    }

    private static void InsertMemory(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string memoryId,
        string servantId,
        string text,
        string candidateId,
        DateTimeOffset createdAtUtc)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO memories(memory_id, servant_id, memory_text, is_enabled, source_candidate_id, created_at_utc, updated_at_utc)
            VALUES($id, $servant, $text, 1, $candidate, $created, $updated)
            """;
        command.Parameters.AddWithValue("$id", memoryId);
        command.Parameters.AddWithValue("$servant", servantId);
        command.Parameters.AddWithValue("$text", text);
        command.Parameters.AddWithValue("$candidate", candidateId);
        command.Parameters.AddWithValue("$created", createdAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$updated", createdAtUtc.ToString("O"));
        command.ExecuteNonQuery();
    }

    private static void UpdateCandidate(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string candidateId,
        string status,
        string text,
        DateTimeOffset reviewedAtUtc)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE memory_candidates SET status=$status, candidate_text=$text, reviewed_at_utc=$reviewed WHERE candidate_id=$id";
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$text", text);
        command.Parameters.AddWithValue("$reviewed", reviewedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$id", candidateId);
        command.ExecuteNonQuery();
    }

    private static void DeleteCandidate(SqliteConnection connection, SqliteTransaction transaction, string candidateId, string servantId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM memory_candidates WHERE candidate_id=$id AND servant_id=$servant";
        command.Parameters.AddWithValue("$id", candidateId);
        command.Parameters.AddWithValue("$servant", servantId);
        command.ExecuteNonQuery();
    }

    private static MemoryCandidate? ReadCandidate(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string candidateId,
        string servantId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT candidate_id, servant_id, conversation_id, candidate_text, created_at_utc,
                   source_message_id, appearance_id, status
            FROM memory_candidates WHERE candidate_id=$id AND servant_id=$servant
            """;
        command.Parameters.AddWithValue("$id", candidateId);
        command.Parameters.AddWithValue("$servant", servantId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadCandidate(reader) : null;
    }

    private static MemoryCandidate ReadCandidate(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            ParseUtc(reader.GetString(4)),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            Enum.Parse<MemoryCandidateStatus>(reader.GetString(7), ignoreCase: true));

    private static DateTimeOffset ParseUtc(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
