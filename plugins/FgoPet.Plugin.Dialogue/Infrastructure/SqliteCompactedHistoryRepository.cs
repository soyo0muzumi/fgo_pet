using System.Globalization;
using System.Text;
using FgoPet.Core.Dialogue;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Persistence;

namespace FgoPet.Infrastructure.Dialogue;

public sealed class SqliteCompactedHistoryRepository(RuntimeDatabase database) : ICompactedHistoryRepository
{
    public CompactedHistoryPage Search(ToolScope scope, string query, int afterSequence = 0,
        int maxItems = 8, int maxChars = 6000, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query) || query.EnumerateRunes().Count() > 256 || query.Contains('\0'))
            throw new ArgumentException("Invalid history query.", nameof(query));
        if (afterSequence < 0) throw new ArgumentOutOfRangeException(nameof(afterSequence));
        if (maxItems is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(maxItems));
        return Query(scope, query, null, afterSequence, 0, maxItems, maxChars, cancellationToken);
    }
    public CompactedHistoryPage Read(ToolScope scope, string messageId, int offset = 0,
        int maxChars = 6000, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(messageId) || messageId.Length > 128)
            throw new ArgumentException("Invalid history source.", nameof(messageId));
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        return Query(scope, null, messageId, 0, offset, 1, maxChars, cancellationToken);
    }

    private CompactedHistoryPage Query(ToolScope scope, string? query, string? messageId,
        int afterSequence, int offset, int maxItems, int maxChars, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(scope);
        if (string.IsNullOrWhiteSpace(scope.ConversationId)) throw new ArgumentException("Invalid conversation scope.");
        if (maxChars is < 1 or > 6000) throw new ArgumentOutOfRangeException(nameof(maxChars));
        var conversationScope = new ConversationScope(scope.RoleId, scope.ProjectId);
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        // Validate coverage and read its original sources in the same SQLite snapshot.
        // A legacy/stale summary never authorizes access to an arbitrary prefix.
        var snapshot = SqliteConversationContextStore.Read(connection, transaction, conversationScope, scope.ConversationId);
        token.ThrowIfCancellationRequested();
        if (snapshot.Summary is null) return new(snapshot.ProjectionRevision, 0, []);
        var coverage = snapshot.Summary.CoveredThroughSequence;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT m.message_id,m.sequence,m.role,m.status,m.created_at_utc,m.text,
              CASE WHEN $query IS NULL THEN 0 ELSE max(0,instr(m.text,$query)-81) END
            FROM chat_messages m JOIN conversations c ON c.conversation_id=m.conversation_id
            WHERE c.conversation_id=$conversation AND c.servant_id=$role AND c.project_id IS $project
              AND m.servant_id=c.servant_id AND m.sequence<=$coverage AND m.sequence>$after
              AND m.role IN ('user','assistant') AND m.status IN ('completed','failed','cancelled')
              AND ($message IS NULL OR m.message_id=$message)
              AND ($query IS NULL OR instr(m.text,$query)>0)
            ORDER BY m.sequence LIMIT $limit
            """;
        command.Parameters.AddWithValue("$conversation", scope.ConversationId);
        command.Parameters.AddWithValue("$role", scope.RoleId);
        command.Parameters.AddWithValue("$project", (object?)scope.ProjectId ?? DBNull.Value);
        command.Parameters.AddWithValue("$coverage", coverage);
        command.Parameters.AddWithValue("$after", afterSequence);
        command.Parameters.AddWithValue("$message", (object?)messageId ?? DBNull.Value);
        command.Parameters.AddWithValue("$query", (object?)query ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", maxItems + 1);
        using var cancellation = token.Register(command.Cancel);
        using var reader = command.ExecuteReader();
        var items = new List<CompactedHistoryItem>();
        var remaining = maxChars;
        int? next = null;
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            if (items.Count == maxItems || remaining == 0)
            {
                next = items[^1].Sequence;
                break;
            }
            var raw = reader.GetString(5);
            var start = messageId is null ? reader.GetInt32(6) : offset;
            var runes = raw.EnumerateRunes().ToArray();
            if (start > runes.Length) throw new ArgumentOutOfRangeException(nameof(offset));
            var count = Math.Min(remaining, runes.Length - start);
            var text = string.Concat(runes.Skip(start).Take(count).Select(rune => rune.ToString()));
            items.Add(new(reader.GetString(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture), text, start,
                start + count < runes.Length ? start + count : null));
            remaining -= count;
        }
        token.ThrowIfCancellationRequested();
        return new(snapshot.ProjectionRevision, coverage, items, next);
    }
}
