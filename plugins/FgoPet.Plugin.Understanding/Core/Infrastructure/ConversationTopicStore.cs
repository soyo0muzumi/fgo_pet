using System.IO;
using System.Text.Json;
using FgoPet.Core.Dialogue;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Understanding.Contracts;
using Microsoft.Data.Sqlite;

namespace FgoPet.Understanding;

/// <summary>SQLite-backed storage for short-term conversation topics.</summary>
public sealed class ConversationTopicStore : IShortTermTopicStore, IDerivedConversationData
{
    private readonly RuntimeDatabase _database;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private bool _schemaReady;

    public ConversationTopicStore(RuntimeDatabase database, TimeProvider? clock = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _clock = clock ?? TimeProvider.System;
    }

    public void SaveTopics(ConversationScope scope, string conversationId, IReadOnlyList<ConversationTopic> topics)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (string.IsNullOrWhiteSpace(conversationId)) throw new ArgumentException("CONVERSATION_ID_REQUIRED", nameof(conversationId));
        if (topics is null || topics.Count == 0) return;
        if (topics.Any(topic => topic is null || topic.RoleId != scope.ServantId ||
            topic.ProjectId != scope.ProjectId || topic.ConversationId != conversationId ||
            string.IsNullOrWhiteSpace(topic.Id) || topic.Sources is null || topic.Sources.Count is < 1 or > 32))
            throw new ArgumentException("CONVERSATION_TOPIC_SCOPE_OR_SOURCES_INVALID", nameof(topics));

        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            EnsureSchema();
            using var db = _database.Open();
            using var tx = db.BeginTransaction();
            Prune(db, tx, now);

            foreach (var topic in topics)
            {
                var state = topic.Tense == "concluded" ? 1 : 0;
                var topicId = topic.Id;
                using var existingCmd = db.CreateCommand();
                existingCmd.Transaction = tx;
                existingCmd.CommandText = "SELECT revision, topic_json FROM conversation_topics WHERE scope=$scope AND conversation_id=$conv AND topic_id=$topic";
                existingCmd.Parameters.AddWithValue("$scope", scope.Key);
                existingCmd.Parameters.AddWithValue("$conv", conversationId);
                existingCmd.Parameters.AddWithValue("$topic", topicId);

                int revision = 1;
                using (var reader = existingCmd.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        var prevRev = reader.GetInt32(0);
                        revision = prevRev + 1;
                    }
                }

                var updated = topic with { Revision = revision };
                var json = JsonSerializer.Serialize(updated);

                using var upsert = db.CreateCommand();
                upsert.Transaction = tx;
                upsert.CommandText = """
                    INSERT INTO conversation_topics(scope, conversation_id, topic_id, revision, active_until, state, topic_json)
                    VALUES($scope, $conv, $topic, $rev, $until, $state, $json)
                    ON CONFLICT(scope, conversation_id, topic_id) DO UPDATE SET
                      revision=$rev, active_until=$until, state=$state, topic_json=$json
                    """;
                upsert.Parameters.AddWithValue("$scope", scope.Key);
                upsert.Parameters.AddWithValue("$conv", conversationId);
                upsert.Parameters.AddWithValue("$topic", topicId);
                upsert.Parameters.AddWithValue("$rev", revision);
                upsert.Parameters.AddWithValue("$until", updated.ActiveUntil.UtcTicks);
                upsert.Parameters.AddWithValue("$state", state);
                upsert.Parameters.AddWithValue("$json", json);
                upsert.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    public IReadOnlyList<ConversationTopic> GetActiveTopics(ConversationScope scope, string? conversationId = null)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            EnsureSchema();
            using var db = _database.Open();
            using var tx = db.BeginTransaction();
            Prune(db, tx, now);

            using var cmd = db.CreateCommand();
            cmd.Transaction = tx;
            if (string.IsNullOrWhiteSpace(conversationId))
            {
                cmd.CommandText = "SELECT topic_json FROM conversation_topics WHERE scope=$scope AND state=0 AND active_until>$now";
                cmd.Parameters.AddWithValue("$scope", scope.Key);
                cmd.Parameters.AddWithValue("$now", now.UtcTicks);
            }
            else
            {
                cmd.CommandText = "SELECT topic_json FROM conversation_topics WHERE scope=$scope AND conversation_id=$conv AND state=0 AND active_until>$now";
                cmd.Parameters.AddWithValue("$scope", scope.Key);
                cmd.Parameters.AddWithValue("$conv", conversationId);
                cmd.Parameters.AddWithValue("$now", now.UtcTicks);
            }

            var list = new List<ConversationTopic>();
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    try
                    {
                        var t = JsonSerializer.Deserialize<ConversationTopic>(reader.GetString(0));
                        if (t is not null) list.Add(t);
                    }
                    catch
                    {
                        // Ignore malformed rows
                    }
                }
            }
            tx.Commit();
            return list;
        }
    }

    public void RetractTopic(ConversationScope scope, string conversationId, string topicId)
    {
        ArgumentNullException.ThrowIfNull(scope);
        lock (_gate)
        {
            EnsureSchema();
            using var db = _database.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "UPDATE conversation_topics SET state=1 WHERE scope=$scope AND conversation_id=$conv AND topic_id=$topic";
            cmd.Parameters.AddWithValue("$scope", scope.Key);
            cmd.Parameters.AddWithValue("$conv", conversationId);
            cmd.Parameters.AddWithValue("$topic", topicId);
            cmd.ExecuteNonQuery();
        }
    }

    public void DeleteScope(ConversationScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        lock (_gate)
        {
            EnsureSchema();
            using var db = _database.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "DELETE FROM conversation_topics WHERE scope=$scope";
            cmd.Parameters.AddWithValue("$scope", scope.Key);
            cmd.ExecuteNonQuery();
        }
    }

    public void ForgetConversation(string conversationId, string roleId)
    {
        lock (_gate)
        {
            EnsureSchema();
            using var db = _database.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "DELETE FROM conversation_topics WHERE conversation_id=$conv AND json_extract(topic_json,'$.RoleId')=$role";
            cmd.Parameters.AddWithValue("$conv", conversationId);
            cmd.Parameters.AddWithValue("$role", roleId);
            cmd.ExecuteNonQuery();
        }
    }

    public void ForgetAll()
    {
        lock (_gate)
        {
            EnsureSchema();
            using var db = _database.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "DELETE FROM conversation_topics";
            cmd.ExecuteNonQuery();
        }
    }

    public JsonElement Export()
    {
        lock (_gate)
        {
            EnsureSchema();
            using var db = _database.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT topic_json FROM conversation_topics WHERE state=0";
            var list = new List<JsonElement>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                try
                {
                    list.Add(JsonSerializer.Deserialize<JsonElement>(reader.GetString(0)));
                }
                catch { }
            }
            return JsonSerializer.SerializeToElement(list);
        }
    }

    public void NormalizeRestoredCopy(string validatedStagingDatabasePath)
    {
        var fullPath = Path.GetFullPath(validatedStagingDatabasePath);
        if (string.Equals(fullPath, _database.DatabasePath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("TOPIC_STORE_RESTORE_REQUIRES_STAGING");
        lock (_gate)
        {
            EnsureSchema();
            var stagedDatabase = new RuntimeDatabase(fullPath, pooling: false);
            new ConversationTopicStore(stagedDatabase, _clock).EnsureSchema();
            using var staged = stagedDatabase.Open();
            using var tx = staged.BeginTransaction();
            using var cmd = staged.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM conversation_topics";
            cmd.ExecuteNonQuery();
            tx.Commit();
        }
    }

    private void EnsureSchema()
    {
        if (_schemaReady) return;
        new RuntimeDatabaseMigrator(_database).MigrateModule("conversation-topics", [new(1, """
            CREATE TABLE conversation_topics(
                scope TEXT NOT NULL,
                conversation_id TEXT NOT NULL,
                topic_id TEXT NOT NULL,
                revision INTEGER NOT NULL,
                active_until INTEGER NOT NULL,
                state INTEGER NOT NULL,
                topic_json TEXT NOT NULL,
                PRIMARY KEY(scope, conversation_id, topic_id)
            );
            CREATE INDEX ix_conversation_topics_expiry ON conversation_topics(active_until);
            """)]);
        _schemaReady = true;
    }

    private static void Prune(SqliteConnection db, SqliteTransaction tx, DateTimeOffset now)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM conversation_topics WHERE active_until<=$now";
        cmd.Parameters.AddWithValue("$now", now.UtcTicks);
        cmd.ExecuteNonQuery();
    }
}
