using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using FgoPet.Core.Dialogue;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace FgoPet.Infrastructure.Dialogue;

/// <summary>Local, disposable associations over exact, completed user originals.</summary>
internal sealed class SqliteExperienceEventStore(RuntimeDatabase database) : IDerivedConversationData
{
    private const int MaxMembers = 16;
    private const int MaxEventsPerScope = 256;
    private readonly object schemaGate = new();
    private bool schemaReady;

    private void EnsureSchema()
    {
        lock (schemaGate)
        {
            if (schemaReady) return;
            new RuntimeDatabaseMigrator(database).MigrateModule("experience-index", [new(1, """
                CREATE TABLE experience_events(
                  event_id TEXT PRIMARY KEY NOT NULL,
                  servant_id TEXT NOT NULL,
                  project_id TEXT NULL);
                CREATE INDEX ix_experience_events_scope ON experience_events(servant_id,project_id);
                CREATE TABLE experience_event_members(
                  event_id TEXT NOT NULL REFERENCES experience_events(event_id) ON DELETE CASCADE,
                  message_id TEXT NOT NULL UNIQUE REFERENCES chat_messages(message_id) ON DELETE CASCADE,
                  source_version TEXT NOT NULL CHECK(length(source_version)=64),
                  PRIMARY KEY(event_id,message_id));
                CREATE TRIGGER experience_event_member_scope BEFORE INSERT ON experience_event_members
                WHEN NOT EXISTS(
                  SELECT 1 FROM experience_events e JOIN chat_messages m ON m.message_id=NEW.message_id
                  JOIN conversations c ON c.conversation_id=m.conversation_id
                  WHERE e.event_id=NEW.event_id AND m.servant_id=e.servant_id AND c.servant_id=e.servant_id
                    AND c.project_id IS e.project_id AND m.status='completed' AND m.role='user')
                BEGIN SELECT RAISE(ABORT,'EXPERIENCE_MEMBER_SCOPE'); END;
                CREATE TRIGGER experience_event_member_changed AFTER UPDATE OF text,status,role,created_at_utc,sequence,servant_id,conversation_id ON chat_messages
                WHEN OLD.text IS NOT NEW.text OR OLD.status IS NOT NEW.status OR OLD.role IS NOT NEW.role
                  OR OLD.created_at_utc IS NOT NEW.created_at_utc OR OLD.sequence IS NOT NEW.sequence
                  OR OLD.servant_id IS NOT NEW.servant_id OR OLD.conversation_id IS NOT NEW.conversation_id
                BEGIN DELETE FROM experience_event_members WHERE message_id=OLD.message_id; END;
                CREATE TRIGGER experience_event_scope_changed AFTER UPDATE OF servant_id,project_id ON conversations
                WHEN OLD.servant_id IS NOT NEW.servant_id OR OLD.project_id IS NOT NEW.project_id
                BEGIN
                  DELETE FROM experience_event_members WHERE message_id IN
                    (SELECT message_id FROM chat_messages WHERE conversation_id=OLD.conversation_id);
                END;
                CREATE TRIGGER experience_event_prune AFTER DELETE ON experience_event_members BEGIN
                  DELETE FROM experience_events WHERE event_id=OLD.event_id AND
                    (SELECT COUNT(DISTINCT m.conversation_id) FROM experience_event_members a
                     JOIN chat_messages m ON m.message_id=a.message_id WHERE a.event_id=OLD.event_id)<2;
                END;
                """)]);
            schemaReady = true;
        }
    }

    internal string Consolidate(ToolScope ended, DateTimeOffset now, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        EnsureSchema();
        using var db = database.Open();
        using var tx = db.BeginTransaction();
        var scope = new ConversationScope(ended.RoleId, ended.ProjectId);
        var current = ReadCandidates(db, tx, scope, ended.ConversationId, since: null);
        if (current.Count == 0) return "no_source";
        var previous = ReadCandidates(db, tx, scope, ended.ConversationId, now.AddDays(-30));

        var matches = (from recent in current
                       from prior in previous
                       let score = Match(recent, prior)
                       where score > 0
                       orderby score descending, prior.Anchor.CreatedAtUtc descending
                       select (Recent: recent, Prior: prior)).ToArray();
        token.ThrowIfCancellationRequested();
        if (matches.Length == 0) return "no_match";
        // Distinct established events and distinct unlinked conversations remain separate possibilities.
        if (matches.Select(pair => pair.Prior.EventId ?? "conversation:" + pair.Prior.Anchor.ConversationId)
                .Distinct(StringComparer.Ordinal).Take(2).Count() > 1 ||
            matches.SelectMany(pair => new[] { pair.Recent.EventId, pair.Prior.EventId })
                .OfType<string>().Distinct(StringComparer.Ordinal).Take(2).Count() > 1)
            return "ambiguous";

        var (recentAnchor, priorAnchor) = matches[0];
        var eventId = recentAnchor.EventId ?? priorAnchor.EventId;
        if (eventId is null)
        {
            using var count = Command(db, tx, """
                SELECT COUNT(*) FROM experience_events WHERE servant_id=$role AND project_id IS $project
                """, ("$role", scope.ServantId), ("$project", scope.ProjectId));
            if ((long)count.ExecuteScalar()! >= MaxEventsPerScope) return "scope_limit";
            eventId = "event-" + Guid.NewGuid().ToString("N");
            using var create = Command(db, tx, """
                INSERT INTO experience_events(event_id,servant_id,project_id) VALUES($event,$role,$project)
                """, ("$event", eventId), ("$role", scope.ServantId), ("$project", scope.ProjectId));
            create.ExecuteNonQuery();
        }
        else
        {
            using var count = Command(db, tx, """
                SELECT COUNT(*) FROM experience_event_members WHERE event_id=$event
                """, ("$event", eventId));
            if ((long)count.ExecuteScalar()! >= MaxMembers &&
                (recentAnchor.EventId is null || priorAnchor.EventId is null)) return "member_limit";
        }

        var added = AddMember(db, tx, eventId, priorAnchor) + AddMember(db, tx, eventId, recentAnchor);
        token.ThrowIfCancellationRequested();
        tx.Commit();
        return added == 0 ? "unchanged" : "linked";
    }

    private static int AddMember(SqliteConnection db, SqliteTransaction tx, string eventId, Candidate candidate)
    {
        if (candidate.EventId == eventId) return 0;
        using var add = Command(db, tx, """
            INSERT INTO experience_event_members(event_id,message_id,source_version) VALUES($event,$id,$version)
            """, ("$event", eventId), ("$id", candidate.Anchor.MessageId),
            ("$version", SqliteExperienceSourceReader.Fingerprint(candidate.Anchor.MessageId,
                ChatMessageRole.User, candidate.Text, candidate.Anchor.CreatedAtUtc)));
        return add.ExecuteNonQuery();
    }

    private static IReadOnlyList<Candidate> ReadCandidates(SqliteConnection db, SqliteTransaction tx,
        ConversationScope scope, string ended, DateTimeOffset? since)
    {
        const string current = "c.conversation_id=$ended";
        const string prior = "c.conversation_id<>$ended AND c.updated_at_utc >= $since";
        using var read = Command(db, tx, $"""
            WITH selected AS (
              SELECT c.conversation_id FROM conversations c
              WHERE c.servant_id=$role AND c.project_id IS $project AND c.status='active'
                AND {(since is null ? current : prior)}
              ORDER BY c.updated_at_utc DESC,c.conversation_id LIMIT 12
            )
            SELECT m.conversation_id,m.message_id,m.sequence,m.created_at_utc,m.text,a.event_id
            FROM selected s JOIN chat_messages m ON m.message_id IN (
              SELECT x.message_id FROM chat_messages x
              WHERE x.conversation_id=s.conversation_id AND x.servant_id=$role
                AND x.role='user' AND x.status='completed' AND length(CAST(x.text AS BLOB)) BETWEEN 1 AND 6000
              ORDER BY x.sequence DESC LIMIT 6
            )
            LEFT JOIN experience_event_members a ON a.message_id=m.message_id
            ORDER BY m.created_at_utc DESC,m.conversation_id,m.sequence DESC
            """, ("$role", scope.ServantId), ("$project", scope.ProjectId), ("$ended", ended),
            ("$since", since?.ToUniversalTime().ToString("O")));
        using var rows = read.ExecuteReader();
        var candidates = new List<Candidate>();
        while (rows.Read())
        {
            var text = rows.GetString(4);
            if (text.Length > 2000 || text.Contains('\0')) continue;
            var anchor = Anchor(rows, 0);
            var identifiers = Regex.Matches(text, @"[A-Za-z0-9_][A-Za-z0-9_.-]{3,63}", RegexOptions.CultureInvariant)
                .Select(match => match.Value.TrimEnd('.', '-'))
                .Where(term => term.Length >= 4 && term.Any(char.IsAsciiLetter) &&
                    term.Any(ch => char.IsAsciiDigit(ch) || ch is '_' or '.' or '-'))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var phrases = Regex.Matches(text, @"[\u3400-\u9fff]{8,}", RegexOptions.CultureInvariant)
                .SelectMany(match => HanPhrases(match.Value))
                .ToHashSet(StringComparer.Ordinal);
            candidates.Add(new(anchor, text, rows.IsDBNull(5) ? null : rows.GetString(5), identifiers, phrases));
        }
        return candidates;
    }

    private static IEnumerable<string> HanPhrases(string run)
    {
        for (var start = 0; start + 8 <= run.Length; start++)
        {
            yield return run.Substring(start, 8);
            if (start + 16 <= run.Length) yield return run.Substring(start, 16);
        }
    }

    private static int Match(Candidate current, Candidate prior)
    {
        if (string.Equals(current.Text.Trim(), prior.Text.Trim(), StringComparison.OrdinalIgnoreCase)) return 0;
        var identifiers = current.Identifiers.Count(prior.Identifiers.Contains);
        // Require corroboration beyond an isolated name. Time restricts the search window,
        // and never breaks an ambiguity between separate candidate events.
        if (identifiers >= 2) return identifiers;
        if (current.Phrases.Any(phrase => phrase.Length == 16 && prior.Phrases.Contains(phrase))) return 2;
        return identifiers == 1 && current.Phrases.Overlaps(prior.Phrases) ? 1 : 0;
    }

    internal IReadOnlyList<ExperienceEventMember> Related(ConversationScope scope, string excludedConversation,
        string messageId, string sourceVersion)
    {
        using var db = database.Open();
        if (!HasSchema(db)) return [];
        using var read = Command(db, null, """
            SELECT m.conversation_id,m.message_id,m.sequence,m.created_at_utc,a.source_version,a.event_id
            FROM experience_event_members root JOIN experience_events e ON e.event_id=root.event_id
            JOIN chat_messages origin ON origin.message_id=root.message_id
            JOIN conversations root_conversation ON root_conversation.conversation_id=origin.conversation_id
            JOIN experience_event_members a ON a.event_id=e.event_id
            JOIN chat_messages m ON m.message_id=a.message_id
            JOIN conversations c ON c.conversation_id=m.conversation_id
            WHERE root.message_id=$id AND root.source_version=$version
              AND root_conversation.status='active' AND root_conversation.servant_id=$role
              AND root_conversation.project_id IS $project AND origin.servant_id=$role
              AND origin.status='completed' AND origin.role='user'
              AND e.servant_id=$role AND e.project_id IS $project AND m.servant_id=$role
              AND c.servant_id=$role AND c.project_id IS $project AND c.status='active'
              AND m.status='completed' AND m.role='user'
              AND m.conversation_id<>$excluded AND a.message_id<>$id
            ORDER BY m.created_at_utc,m.conversation_id,m.sequence LIMIT 6
            """, ("$role", scope.ServantId), ("$project", scope.ProjectId), ("$id", messageId),
            ("$version", sourceVersion), ("$excluded", excludedConversation));
        using var rows = read.ExecuteReader();
        var result = new List<ExperienceEventMember>();
        while (rows.Read()) result.Add(new(rows.GetString(5), Anchor(rows, 0), rows.GetString(4)));
        return result;
    }

    internal bool IsMember(ConversationScope scope, string eventId, string messageId, string sourceVersion)
    {
        using var db = database.Open();
        if (!HasSchema(db)) return false;
        using var read = Command(db, null, """
            SELECT EXISTS(
              SELECT 1 FROM experience_event_members a JOIN experience_events e ON e.event_id=a.event_id
              JOIN chat_messages m ON m.message_id=a.message_id JOIN conversations c ON c.conversation_id=m.conversation_id
              WHERE a.event_id=$event AND a.message_id=$id AND a.source_version=$version
                AND e.servant_id=$role AND e.project_id IS $project AND m.servant_id=$role
                AND c.servant_id=$role AND c.project_id IS $project AND c.status='active'
                AND m.status='completed' AND m.role='user'
                AND EXISTS(
                  SELECT 1 FROM experience_event_members other
                  JOIN chat_messages om ON om.message_id=other.message_id
                  JOIN conversations oc ON oc.conversation_id=om.conversation_id
                  WHERE other.event_id=e.event_id AND oc.conversation_id<>c.conversation_id
                    AND oc.servant_id=$role AND oc.project_id IS $project AND oc.status='active'
                    AND om.servant_id=$role AND om.status='completed' AND om.role='user'))
            """, ("$event", eventId), ("$id", messageId), ("$version", sourceVersion),
            ("$role", scope.ServantId), ("$project", scope.ProjectId));
        return (long)read.ExecuteScalar()! == 1;
    }

    public void ForgetConversation(string conversationId, string roleId)
    {
        using var db = database.Open();
        if (!HasSchema(db)) return;
        using var command = Command(db, null, """
            DELETE FROM experience_event_members WHERE message_id IN
              (SELECT message_id FROM chat_messages WHERE conversation_id=$conversation AND servant_id=$role)
            """, ("$conversation", conversationId), ("$role", roleId));
        command.ExecuteNonQuery();
    }

    public void ForgetAll() => Clear(database);

    public void NormalizeRestoredCopy(string validatedStagingDatabasePath) =>
        Clear(new RuntimeDatabase(validatedStagingDatabasePath, pooling: false));

    private static void Clear(RuntimeDatabase target)
    {
        using var db = target.Open();
        if (!HasSchema(db)) return;
        using var command = Command(db, null, "DELETE FROM experience_events");
        command.ExecuteNonQuery();
    }

    public JsonElement Export() =>
        JsonSerializer.SerializeToElement(new { owner = "experience_index", members = Array.Empty<object>() });

    private static bool HasSchema(SqliteConnection db)
    {
        using var command = Command(db, null, """
            SELECT EXISTS(SELECT 1 FROM sqlite_schema WHERE type='table' AND name='experience_events')
            """);
        return (long)command.ExecuteScalar()! == 1;
    }

    private static HistoryAnchor Anchor(SqliteDataReader row, int start) =>
        new(row.GetString(start), row.GetString(start + 1), row.GetInt32(start + 2),
            DateTimeOffset.Parse(row.GetString(start + 3), CultureInfo.InvariantCulture));

    private static SqliteCommand Command(SqliteConnection db, SqliteTransaction? tx, string sql,
        params (string Name, object? Value)[] args)
    {
        var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    private sealed record Candidate(HistoryAnchor Anchor, string Text, string? EventId,
        HashSet<string> Identifiers, HashSet<string> Phrases);
}

internal sealed record ExperienceEventMember(string EventId, HistoryAnchor Anchor, string SourceVersion);
