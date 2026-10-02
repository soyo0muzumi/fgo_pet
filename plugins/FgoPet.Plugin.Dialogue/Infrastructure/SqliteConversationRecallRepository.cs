using System.Globalization;
using System.Text;
using FgoPet.Core.Dialogue;
using FgoPet.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace FgoPet.Infrastructure.Dialogue;

/// <summary>Hermes session_search: scoped browse, literal discovery, then bounded anchored reads.</summary>
public sealed class SqliteConversationRecallRepository(RuntimeDatabase database) : IConversationRecallRepository
{
    private const string ScopeWhere = """
        c.servant_id=$servant AND m.servant_id=c.servant_id AND c.project_id IS $project
        AND c.conversation_id<>$excluded AND m.role IN ('user','assistant') AND m.status='completed'
        """;
    private const string Projection = """
        m.conversation_id,m.message_id,m.sequence,m.created_at_utc,
        (SELECT substr(t.text,1,50) FROM chat_messages t WHERE t.conversation_id=c.conversation_id
         AND t.servant_id=c.servant_id AND t.role='user' AND t.status='completed' ORDER BY t.sequence LIMIT 1),
        substr(m.text,1,800),length(m.text)>800
        """;

    public IReadOnlyList<HistoryHit> Browse(ConversationScope scope, string excludedConversationId, int limit = 10) =>
        Query(scope, excludedConversationId, $"""
            SELECT {Projection} FROM conversations c JOIN chat_messages m ON m.conversation_id=c.conversation_id
            WHERE {ScopeWhere} AND m.sequence=(SELECT MAX(x.sequence) FROM chat_messages x
              WHERE x.conversation_id=c.conversation_id AND x.servant_id=c.servant_id AND x.status='completed' AND x.role IN ('user','assistant'))
            ORDER BY c.updated_at_utc DESC,c.conversation_id DESC LIMIT $limit
            """, Math.Clamp(limit, 1, 10));

    public IReadOnlyList<HistoryHit> Discover(ConversationScope scope, string excludedConversationId, string query, int limit = 20)
    {
        query = string.Concat((query ?? string.Empty).Trim().EnumerateRunes().Take(128).Select(rune => rune.ToString()));
        if (string.IsNullOrWhiteSpace(query)) return Browse(scope, excludedConversationId, Math.Min(limit, 10));
        var shortQuery = query.EnumerateRunes().Count() < 3;
        var sql = shortQuery ? $"""
            WITH recent AS (SELECT conversation_id FROM conversations WHERE servant_id=$servant AND project_id IS $project
                AND conversation_id<>$excluded ORDER BY updated_at_utc DESC,conversation_id DESC LIMIT 10),
            bounded AS (SELECT m.rowid FROM chat_messages m JOIN conversations c ON c.conversation_id=m.conversation_id
                WHERE {ScopeWhere} AND c.conversation_id IN (SELECT conversation_id FROM recent)
                ORDER BY c.updated_at_utc DESC,c.conversation_id DESC,m.sequence DESC LIMIT 200)
            SELECT {Projection} FROM bounded b JOIN chat_messages m ON m.rowid=b.rowid JOIN conversations c ON c.conversation_id=m.conversation_id
            WHERE instr(m.text,$query)>0 ORDER BY c.updated_at_utc DESC,c.conversation_id DESC,m.sequence DESC LIMIT $limit
            """ : $"""
            SELECT {Projection} FROM chat_message_search f JOIN chat_messages m ON m.rowid=f.rowid
            JOIN conversations c ON c.conversation_id=m.conversation_id
            WHERE {ScopeWhere} AND chat_message_search MATCH $query
            ORDER BY bm25(chat_message_search),c.updated_at_utc DESC,c.conversation_id DESC,m.sequence DESC LIMIT $limit
            """;
        var hits = Query(scope, excludedConversationId, sql, Math.Clamp(limit, 1, 20),
            shortQuery ? query : "\"" + query.Replace("\"", "\"\"") + "\"");
        var conversations = hits.Select(hit => hit.Anchor.ConversationId).Distinct().Take(5).ToHashSet();
        return hits.Where(hit => conversations.Contains(hit.Anchor.ConversationId)).ToArray();
    }

    private IReadOnlyList<HistoryHit> Query(ConversationScope scope, string excluded, string sql, int limit, string? query = null)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        BindScope(command, scope);
        command.Parameters.AddWithValue("$excluded", excluded);
        command.Parameters.AddWithValue("$limit", limit);
        if (query is not null) command.Parameters.AddWithValue("$query", query);
        using var reader = command.ExecuteReader();
        var hits = new List<HistoryHit>();
        while (reader.Read()) hits.Add(new(new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), Parse(reader.GetString(3))),
            reader.IsDBNull(4) ? "新会话" : reader.GetString(4), reader.GetString(5), reader.GetBoolean(6)));
        return hits;
    }

    public HistoryReadPage Read(ConversationScope scope, HistoryAnchor anchor, int maxChars = 6000, HistoryReadCursor? cursor = null)
    {
        if (maxChars is < 2 or > 6000) throw new ArgumentOutOfRangeException(nameof(maxChars));
        if (cursor is not null && (cursor.ScopeKey != scope.Key || cursor.ConversationId != anchor.ConversationId ||
            cursor.Sequence < 1 || cursor.TextOffset < 0))
            throw new ArgumentException("History cursor belongs to another scope or is invalid.", nameof(cursor));
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        BindScope(command, scope);
        command.Parameters.AddWithValue("$conversation", anchor.ConversationId);
        command.Parameters.AddWithValue("$message", anchor.MessageId);
        command.Parameters.AddWithValue("$sequence", anchor.Sequence);
        command.CommandText = """
            SELECT m.created_at_utc FROM chat_messages m JOIN conversations c ON c.conversation_id=m.conversation_id
            WHERE c.servant_id=$servant AND m.servant_id=c.servant_id AND c.project_id IS $project
              AND m.conversation_id=$conversation AND m.message_id=$message AND m.sequence=$sequence
              AND m.status='completed' AND m.role IN ('user','assistant')
            """;
        if (command.ExecuteScalar() is not string date || Parse(date) != anchor.CreatedAtUtc) return new([], null);
        command.CommandText = """
            SELECT COALESCE(MAX(sequence),1) FROM chat_messages WHERE conversation_id=$conversation
              AND servant_id=$servant AND role='user' AND status='completed' AND sequence <
                (SELECT COALESCE(MAX(sequence),$sequence) FROM chat_messages WHERE conversation_id=$conversation
                  AND servant_id=$servant AND role='user' AND sequence<=$sequence)
            """;
        var start = cursor?.Sequence ?? Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        command.Parameters.AddWithValue("$start", start);
        command.CommandText = """
            SELECT m.message_id,m.sequence,m.created_at_utc,m.text,
              (SELECT substr(t.text,1,50) FROM chat_messages t WHERE t.conversation_id=c.conversation_id
               AND t.servant_id=c.servant_id AND t.role='user' AND t.status='completed' ORDER BY t.sequence LIMIT 1)
            FROM chat_messages m JOIN conversations c ON c.conversation_id=m.conversation_id
            WHERE c.servant_id=$servant AND m.servant_id=c.servant_id AND c.project_id IS $project
              AND m.conversation_id=$conversation AND m.status='completed' AND m.role IN ('user','assistant') AND m.sequence>=$start
            ORDER BY m.sequence
            """;
        using var reader = command.ExecuteReader();
        var items = new List<HistoryHit>();
        var remaining = maxChars;
        HistoryReadCursor? next = null;
        while (reader.Read())
        {
            var sequence = reader.GetInt32(1);
            var full = reader.GetString(3);
            var offset = cursor is not null && sequence == cursor.Sequence ? cursor.TextOffset : 0;
            if (offset > full.Length || offset > 0 && offset < full.Length && char.IsLowSurrogate(full[offset]))
                throw new ArgumentException("Invalid history text offset.", nameof(cursor));
            if (remaining < 2) { next = new(scope.Key, anchor.ConversationId, sequence, offset); break; }
            var count = Math.Min(remaining, full.Length - offset);
            if (count > 0 && offset + count < full.Length && char.IsHighSurrogate(full[offset + count - 1])) count--;
            items.Add(new(new(anchor.ConversationId, reader.GetString(0), sequence, Parse(reader.GetString(2))),
                reader.IsDBNull(4) ? "新会话" : reader.GetString(4), full.Substring(offset, count), offset + count < full.Length));
            remaining -= count;
            if (offset + count < full.Length) { next = new(scope.Key, anchor.ConversationId, sequence, offset + count); break; }
        }
        return new(items, next);
    }

    private static void BindScope(SqliteCommand command, ConversationScope scope)
    {
        command.Parameters.AddWithValue("$servant", scope.ServantId);
        command.Parameters.AddWithValue("$project", (object?)scope.ProjectId ?? DBNull.Value);
    }
    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
}
