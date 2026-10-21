using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FgoPet.Core.Dialogue;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Persistence;

namespace FgoPet.Infrastructure.Dialogue;

public sealed class SqliteExperienceTimeline(RuntimeDatabase database, IExperienceSourceReader originals) : IExperienceTimeline
{
    private readonly object _schemaGate = new();
    private bool _schemaReady;
    private void EnsureSchema()
    {
        lock (_schemaGate)
        {
            if (_schemaReady) return;
            new RuntimeDatabaseMigrator(database).MigrateModule("experience-sources", [new(1, """
                ALTER TABLE chat_messages ADD COLUMN experience_sequence INTEGER NOT NULL DEFAULT 0;
                UPDATE chat_messages SET experience_sequence=rowid;
                CREATE TABLE experience_source_clock(singleton INTEGER PRIMARY KEY CHECK(singleton=1), sequence INTEGER NOT NULL);
                INSERT INTO experience_source_clock VALUES(1,(SELECT COALESCE(MAX(experience_sequence),0) FROM chat_messages));
                CREATE TRIGGER experience_source_insert AFTER INSERT ON chat_messages BEGIN
                  UPDATE experience_source_clock SET sequence=sequence+1 WHERE singleton=1;
                  UPDATE chat_messages SET experience_sequence=(SELECT sequence FROM experience_source_clock WHERE singleton=1) WHERE message_id=NEW.message_id;
                END;
                CREATE TRIGGER experience_source_change AFTER UPDATE OF text,status,role,created_at_utc ON chat_messages
                WHEN NEW.text IS NOT OLD.text OR NEW.status IS NOT OLD.status OR NEW.role IS NOT OLD.role OR NEW.created_at_utc IS NOT OLD.created_at_utc BEGIN
                  UPDATE experience_source_clock SET sequence=sequence+1 WHERE singleton=1;
                  UPDATE chat_messages SET experience_sequence=(SELECT sequence FROM experience_source_clock WHERE singleton=1) WHERE message_id=NEW.message_id;
                END;
                CREATE TRIGGER experience_source_delete AFTER DELETE ON chat_messages BEGIN
                  UPDATE experience_source_clock SET sequence=sequence+1 WHERE singleton=1;
                END;
                """), new(2, """
                CREATE TRIGGER experience_conversation_scope_change AFTER UPDATE OF servant_id,project_id,project_label ON conversations
                WHEN NEW.servant_id IS NOT OLD.servant_id OR NEW.project_id IS NOT OLD.project_id OR NEW.project_label IS NOT OLD.project_label BEGIN
                  UPDATE experience_source_clock SET sequence=sequence+1 WHERE singleton=1;
                  UPDATE chat_messages SET experience_sequence=(SELECT sequence FROM experience_source_clock WHERE singleton=1)
                    WHERE conversation_id=NEW.conversation_id;
                END;
                """), new(3, "CREATE INDEX experience_scan_order ON chat_messages(experience_sequence,message_id);")]);
            _schemaReady = true;
        }
    }
    public long Boundary(ConversationScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        EnsureSchema();
        using var db = database.Open(); using var cmd = db.CreateCommand();
        // The owner clock survives row deletion and advances when a pending message completes.
        // It contains no text or per-message event history. The caller still filters exact scope.
        cmd.CommandText = "SELECT sequence FROM experience_source_clock WHERE singleton=1";
        return (long)cmd.ExecuteScalar()!;
    }
    public long InputRevision(ConversationScope scope)
    {
        EnsureSchema();using var db=database.Open();using var cmd=db.CreateCommand();
        cmd.CommandText="""
            SELECT COALESCE(MAX(m.experience_sequence),0) FROM chat_messages m JOIN conversations c ON c.conversation_id=m.conversation_id
            WHERE m.servant_id=$role AND c.servant_id=$role AND c.project_id IS $project
              AND m.status='completed' AND m.role IN ('user','assistant')
            """;
        cmd.Parameters.AddWithValue("$role",scope.ServantId);cmd.Parameters.AddWithValue("$project",(object?)scope.ProjectId??DBNull.Value);
        return (long)cmd.ExecuteScalar()!;
    }
    public ExperienceScanPage ScanHeads(ConversationScope scope, ExperienceScanCursor cursor, int maxItems = 32,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if(cursor.Sequence<0 || cursor.MessageId is null || cursor.MessageId.Length>128 || cursor.MessageId.Contains('\0') || maxItems is <1 or >32)
            return new(ExperienceSourceStatus.ScopeDenied,[],cursor,false);
        try
        {
            EnsureSchema();using var db=database.Open();using var tx=db.BeginTransaction(deferred:true);
            using var cmd=db.CreateCommand();cmd.Transaction=tx;
            cmd.CommandText="""
                SELECT m.message_id,m.sequence,m.created_at_utc,m.role,
                  CASE WHEN length(CAST(m.text AS BLOB))<=36000 THEN m.text ELSE NULL END,
                  m.experience_sequence,m.conversation_id,c.project_label
                FROM chat_messages m JOIN conversations c ON c.conversation_id=m.conversation_id
                WHERE m.servant_id=$role AND c.servant_id=$role AND c.project_id IS $project
                  AND m.status='completed' AND m.role IN ('user','assistant')
                  AND (m.experience_sequence>$sequence OR (m.experience_sequence=$sequence AND m.message_id COLLATE BINARY>$id))
                ORDER BY m.experience_sequence,m.message_id COLLATE BINARY LIMIT $limit
                """;
            cmd.Parameters.AddWithValue("$role",scope.ServantId);cmd.Parameters.AddWithValue("$project",(object?)scope.ProjectId??DBNull.Value);
            cmd.Parameters.AddWithValue("$sequence",cursor.Sequence);cmd.Parameters.AddWithValue("$id",cursor.MessageId);
            cmd.Parameters.AddWithValue("$limit",maxItems+1);
            using var r=cmd.ExecuteReader();var items=new List<ExperienceSourceHead>();var next=cursor;var examined=0;var more=false;
            while(r.Read())
            {
                token.ThrowIfCancellationRequested();if(examined++==maxItems){more=true;break;}
                next=new(r.GetInt64(5),r.GetString(0));
                // Unsupported/oversized originals are not qualifying intervals. Advance the cursor without reading their bodies.
                if(r.IsDBNull(4))continue;
                var text=r.GetString(4);if(text.Length is <1 or >12000)continue;
                var role=r.GetString(3)=="user"?ChatMessageRole.User:ChatMessageRole.Assistant;
                var at=DateTimeOffset.Parse(r.GetString(2),CultureInfo.InvariantCulture);
                var source=new ConversationSourceSnapshot(new(r.GetString(6),scope.ServantId,scope.ProjectId),r.GetString(0),
                    Fingerprint(r.GetString(0),role,text,at),at,role==ChatMessageRole.User?ConversationSourceKind.User:ConversationSourceKind.Assistant,
                    r.IsDBNull(7)?null:r.GetString(7),r.GetInt64(5));
                items.Add(new(new(r.GetString(6),r.GetString(0),r.GetInt32(1),at),source,text.EnumerateRunes().Count()));
            }
            return new(ExperienceSourceStatus.Available,items,next,more);
        }
        catch(OperationCanceledException){throw;}
        catch(Exception){token.ThrowIfCancellationRequested();return new(ExperienceSourceStatus.Unavailable,[],cursor,false);}
    }
    public ExperienceWindowPage ReadPage(ConversationScope scope, string conversationId, int startSequence = 1,
        ExperienceWindowCursor? cursor = null, int maxItems = 8, int maxScalars = 6000, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(conversationId) || conversationId.Length > 128 || startSequence < 1 || maxItems is < 1 or > 32 || maxScalars is < 1 or > 6000 ||
            cursor is not null && (cursor.StartSequence != startSequence || cursor.Offset is < 0 or > 31 || cursor.ThroughSequence < startSequence || cursor.Version is not { Length: 64 }))
            return new(ExperienceSourceStatus.ScopeDenied, []);
        try
        {
            EnsureSchema();
            using var db = database.Open(); using var tx = db.BeginTransaction(deferred: true); using var cmd = db.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = "SELECT servant_id,project_id,project_label FROM conversations WHERE conversation_id=$conversation";
            cmd.Parameters.AddWithValue("$conversation", conversationId);
            string? projectLabel;
            using (var r = cmd.ExecuteReader())
            {
                if (!r.Read()) return new(ExperienceSourceStatus.Deleted, []);
                if (r.GetString(0) != scope.ServantId || (r.IsDBNull(1) ? null : r.GetString(1)) != scope.ProjectId) return new(ExperienceSourceStatus.ScopeDenied, []);
                projectLabel = r.IsDBNull(2) ? null : r.GetString(2);
            }
            cmd.CommandText = """
                SELECT message_id,sequence,created_at_utc,role,
                  CASE WHEN length(CAST(text AS BLOB))<=36000 THEN text ELSE NULL END,experience_sequence FROM chat_messages
                WHERE conversation_id=$conversation AND servant_id=$role AND sequence >= $start AND sequence <= $through
                  AND status='completed' AND role IN ('user','assistant') ORDER BY sequence LIMIT 33
                """;
            cmd.Parameters.AddWithValue("$role", scope.ServantId); cmd.Parameters.AddWithValue("$start", startSequence);
            cmd.Parameters.AddWithValue("$through", cursor?.ThroughSequence ?? int.MaxValue);
            var messages = new List<(HistoryAnchor Anchor, ConversationSourceSnapshot Source, string Text, long SourceSequence)>();
            var hasLater = false;
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                {
                    token.ThrowIfCancellationRequested();
                    if (messages.Count == 32) { hasLater = true; break; }
                    var role = r.GetString(3) == "user" ? ChatMessageRole.User : ChatMessageRole.Assistant;
                    if (r.IsDBNull(4)) return new(ExperienceSourceStatus.Changed, []);
                    var text = r.GetString(4);
                    if (text.Length is < 1 or > 12000) return new(ExperienceSourceStatus.Changed, []);
                    var time = DateTimeOffset.Parse(r.GetString(2), CultureInfo.InvariantCulture);
                    var id = r.GetString(0);
                    var sourceVersion = Fingerprint(id, role, text, time);
                    messages.Add((new(conversationId, id, r.GetInt32(1), time),
                        new(new(conversationId, scope.ServantId, scope.ProjectId), id, sourceVersion, time,
                            role == ChatMessageRole.User ? ConversationSourceKind.User : ConversationSourceKind.Assistant, projectLabel, r.GetInt64(5)), text, r.GetInt64(5)));
                }
            var through = cursor?.ThroughSequence ?? (messages.Count > 0 ? messages[^1].Anchor.Sequence : startSequence);
            cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM chat_messages WHERE conversation_id=$conversation AND servant_id=$role AND sequence>$through AND status='completed' AND role IN ('user','assistant'))";
            cmd.Parameters["$through"].Value = through;
            hasLater |= (long)cmd.ExecuteScalar()! != 0;
            var version = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
            { Scope = scope.Key, ConversationId = conversationId, Start = startSequence, Through = through, Sources = messages.Select(m => m.Source).ToArray() })));
            if (cursor is not null && cursor.Version != version) return new(ExperienceSourceStatus.Changed, []);
            var offset = cursor?.Offset ?? 0;
            if (offset > messages.Count) return new(ExperienceSourceStatus.Changed, []);
            var items = new List<ExperienceWindowItem>(); var remaining = maxScalars;
            foreach (var message in messages.Skip(offset).Take(maxItems))
            {
                if (remaining == 0) break;
                var scalars = message.Text.EnumerateRunes().ToArray(); var included = Math.Min(remaining, scalars.Length);
                var excerpt = string.Concat(scalars.Take(included).Select(r => r.ToString()));
                items.Add(new(message.Anchor, message.Source, excerpt, included, scalars.Length, included == scalars.Length, message.SourceSequence));
                remaining -= included;
            }
            var nextOffset = offset + items.Count;
            token.ThrowIfCancellationRequested();
            return new(ExperienceSourceStatus.Available, items, nextOffset < messages.Count ? new(startSequence, through, nextOffset, version) : null,
                version, through, hasLater);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { token.ThrowIfCancellationRequested(); return new(ExperienceSourceStatus.Unavailable, []); }
    }
    public ExperienceWindowPage ReadAdjacent(ConversationScope scope, HistoryAnchor anchor, CancellationToken token = default)
    {
        var read = originals.Read(scope, anchor, token);
        if (read.Status != ExperienceSourceStatus.Available) return new(read.Status, []);
        // A bounded nearby window; its coverage is only the messages actually returned.
        return ReadPage(scope, anchor.ConversationId, Math.Max(1, anchor.Sequence - 1), maxItems: 3, token: token);
    }
    public FgoPet.Extensibility.ConversationSourceSnapshot Read(ToolScope scope, string messageId, ConversationSourceKind kind)
    {
        EnsureSchema();
        using var db = database.Open(); using var tx = db.BeginTransaction(deferred: true); using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT m.sequence,m.created_at_utc,c.project_label,m.experience_sequence,
              CASE WHEN length(CAST(m.text AS BLOB))<=36000 THEN m.text ELSE NULL END FROM chat_messages m
            JOIN conversations c ON c.conversation_id=m.conversation_id
            WHERE m.message_id=$id AND m.conversation_id=$conversation AND m.servant_id=$role AND c.servant_id=$role AND c.project_id IS $project
              AND m.status='completed' AND m.role=$kind
            """;
        if (kind is not(ConversationSourceKind.User or ConversationSourceKind.Assistant)) throw new InvalidOperationException("EXPERIENCE_SOURCE_CHANGED");
        cmd.Parameters.AddWithValue("$id", messageId); cmd.Parameters.AddWithValue("$conversation", scope.ConversationId);
        cmd.Parameters.AddWithValue("$role", scope.RoleId); cmd.Parameters.AddWithValue("$project", (object?)scope.ProjectId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$kind", kind == ConversationSourceKind.User ? "user" : "assistant");
        HistoryAnchor anchor; string? label; long revision; string text;
        using (var r = cmd.ExecuteReader())
        {
            if (!r.Read()) throw new InvalidOperationException("EXPERIENCE_SOURCE_CHANGED");
            anchor = new(scope.ConversationId, messageId, r.GetInt32(0), DateTimeOffset.Parse(r.GetString(1), CultureInfo.InvariantCulture));
            label = r.IsDBNull(2) ? null : r.GetString(2); revision = r.GetInt64(3);
            if (r.IsDBNull(4)) throw new InvalidOperationException("EXPERIENCE_SOURCE_CHANGED");
            text = r.GetString(4);
            if (text.Length is < 1 or > 12000) throw new InvalidOperationException("EXPERIENCE_SOURCE_CHANGED");
        }
        return new(scope, messageId, Fingerprint(messageId, kind == ConversationSourceKind.User ? ChatMessageRole.User : ChatMessageRole.Assistant,
            text, anchor.CreatedAtUtc), anchor.CreatedAtUtc, kind, label, revision);
    }
    private static string Fingerprint(string id, ChatMessageRole role, string text, DateTimeOffset time) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { MessageId = id, Role = role, Text = text, CreatedAtUtc = time })));
    public bool IsCurrent(ConversationSourceSnapshot source) => ValidateSource(source) == ExperienceSourceStatus.Available;
    public ExperienceSourceStatus ValidateSource(ConversationSourceSnapshot source)
    {
        try { return source.SourceRevision > 0 && source == Read(source.Scope, source.MessageId, source.Kind) ? ExperienceSourceStatus.Available : ExperienceSourceStatus.Changed; }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException) { return ExperienceSourceStatus.Changed; }
        catch (Exception) { return ExperienceSourceStatus.Unavailable; }
    }
}
