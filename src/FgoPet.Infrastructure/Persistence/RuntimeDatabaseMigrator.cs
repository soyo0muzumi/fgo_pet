using Microsoft.Data.Sqlite;

namespace FgoPet.Infrastructure.Persistence;

/// <summary>Raised when the on-disk schema version is newer than this build supports.</summary>
public sealed class RuntimeDatabaseVersionException(long foundVersion, long maxSupportedVersion)
    : InvalidOperationException($"Runtime database schema version {foundVersion} is newer than the supported version {maxSupportedVersion}.")
{
    public long FoundVersion { get; } = foundVersion;

    public long MaxSupportedVersion { get; } = maxSupportedVersion;
}

/// <summary>
/// Ordered, transactional schema migrations. Each migration runs in one transaction
/// with its row inserted last; a failure rolls back completely. A database at a
/// version this build does not know raises <see cref="RuntimeDatabaseVersionException"/>
/// instead of touching any file.
/// </summary>
public sealed class RuntimeDatabaseMigrator
{
    private static readonly IReadOnlyList<Migration> Migrations = new Migration[]
    {
        new(1, """
            CREATE TABLE focus_presets(
              preset_id TEXT PRIMARY KEY,
              kind TEXT NOT NULL,
              focus_seconds INTEGER NOT NULL CHECK(focus_seconds BETWEEN 300 AND 10800),
              break_seconds INTEGER NOT NULL CHECK(break_seconds BETWEEN 60 AND 3600),
              cycles INTEGER NOT NULL CHECK(cycles BETWEEN 1 AND 12),
              updated_at_utc TEXT NOT NULL);
            CREATE TABLE focus_sessions(
              session_id TEXT PRIMARY KEY,
              status TEXT NOT NULL,
              focus_seconds INTEGER NOT NULL,
              break_seconds INTEGER NOT NULL,
              total_cycles INTEGER NOT NULL,
              current_cycle INTEGER NOT NULL,
              phase TEXT NOT NULL,
              remaining_seconds INTEGER NOT NULL,
              phase_elapsed_seconds INTEGER NOT NULL,
              servant_id TEXT NOT NULL,
              started_at_utc TEXT NOT NULL,
              updated_at_utc TEXT NOT NULL,
              is_current INTEGER NOT NULL CHECK(is_current IN (0,1)));
            CREATE UNIQUE INDEX ux_focus_sessions_current ON focus_sessions(is_current) WHERE is_current=1;
            CREATE TABLE runtime_events(
              event_id TEXT PRIMARY KEY,
              session_id TEXT NOT NULL,
              type TEXT NOT NULL,
              occurred_at_utc TEXT NOT NULL,
              cycle_number INTEGER NOT NULL,
              phase TEXT NOT NULL,
              servant_id TEXT NOT NULL,
              elapsed_seconds INTEGER NOT NULL,
              effective_seconds INTEGER NOT NULL,
              priority INTEGER NOT NULL,
              schema_version INTEGER NOT NULL,
              payload_json TEXT NULL);
            CREATE TABLE timeline_entries(
              entry_id TEXT PRIMARY KEY,
              source_event_id TEXT NOT NULL UNIQUE REFERENCES runtime_events(event_id),
              occurred_at_utc TEXT NOT NULL,
              type TEXT NOT NULL,
              servant_id TEXT NOT NULL,
              elapsed_seconds INTEGER NOT NULL,
              effective_seconds INTEGER NOT NULL,
              bond_level INTEGER NULL);
            CREATE TABLE servant_bonds(
              servant_id TEXT PRIMARY KEY,
              lifetime_focus_seconds INTEGER NOT NULL,
              achieved_level INTEGER NOT NULL,
              policy_version TEXT NOT NULL,
              updated_at_utc TEXT NOT NULL);
            CREATE TABLE bond_ledger(
              ledger_id TEXT PRIMARY KEY,
              source_event_id TEXT NOT NULL UNIQUE REFERENCES runtime_events(event_id),
              servant_id TEXT NOT NULL,
              effective_seconds INTEGER NOT NULL,
              occurred_at_utc TEXT NOT NULL);
            """),
        new(2, """
            CREATE TABLE content_bindings(
              binding_id TEXT PRIMARY KEY,
              servant_id TEXT NOT NULL,
              package_id TEXT NOT NULL,
              package_version TEXT NOT NULL,
              appearance_id TEXT NOT NULL,
              persona_version TEXT NOT NULL,
              knowledge_version TEXT NOT NULL,
              persona_hash TEXT NOT NULL,
              knowledge_hash TEXT NOT NULL,
              created_at_utc TEXT NOT NULL,
              UNIQUE(servant_id, package_id, package_version, appearance_id, persona_version, knowledge_version,
                     persona_hash, knowledge_hash));
            CREATE INDEX ix_content_bindings_servant ON content_bindings(servant_id);
            CREATE TABLE conversations(
              conversation_id TEXT PRIMARY KEY,
              servant_id TEXT NOT NULL,
              created_at_utc TEXT NOT NULL,
              updated_at_utc TEXT NOT NULL,
              status TEXT NOT NULL CHECK(status IN ('active','archived')),
              current_binding_id TEXT NULL REFERENCES content_bindings(binding_id) ON DELETE SET NULL);
            CREATE INDEX ix_conversations_servant_updated
              ON conversations(servant_id, updated_at_utc DESC);
            CREATE TABLE chat_messages(
              message_id TEXT PRIMARY KEY,
              conversation_id TEXT NOT NULL REFERENCES conversations(conversation_id) ON DELETE CASCADE,
              servant_id TEXT NOT NULL,
              sequence INTEGER NOT NULL CHECK(sequence > 0),
              role TEXT NOT NULL CHECK(role IN ('system','user','assistant')),
              text TEXT NOT NULL CHECK(length(text) <= 12000),
              status TEXT NOT NULL CHECK(status IN ('pending','completed','cancelled','failed')),
              created_at_utc TEXT NOT NULL,
              binding_id TEXT NULL REFERENCES content_bindings(binding_id) ON DELETE SET NULL,
              UNIQUE(conversation_id, sequence));
            CREATE INDEX ix_chat_messages_conversation_sequence
              ON chat_messages(conversation_id, sequence);
            CREATE INDEX ix_chat_messages_servant
              ON chat_messages(servant_id, conversation_id);
            CREATE TABLE conversation_summaries(
              summary_id TEXT PRIMARY KEY,
              conversation_id TEXT NOT NULL REFERENCES conversations(conversation_id) ON DELETE CASCADE,
              servant_id TEXT NOT NULL,
              summary_text TEXT NOT NULL CHECK(length(summary_text) <= 6000),
              covered_through_sequence INTEGER NOT NULL CHECK(covered_through_sequence >= 0),
              binding_id TEXT NULL REFERENCES content_bindings(binding_id) ON DELETE SET NULL,
              created_at_utc TEXT NOT NULL,
              updated_at_utc TEXT NOT NULL);
            CREATE INDEX ix_conversation_summaries_servant
              ON conversation_summaries(servant_id, conversation_id);
            CREATE TABLE memory_candidates(
              candidate_id TEXT PRIMARY KEY,
              conversation_id TEXT NOT NULL REFERENCES conversations(conversation_id) ON DELETE CASCADE,
              source_message_id TEXT NULL REFERENCES chat_messages(message_id) ON DELETE CASCADE,
              servant_id TEXT NOT NULL,
              appearance_id TEXT NULL,
              candidate_text TEXT NOT NULL CHECK(length(candidate_text) <= 2000),
              status TEXT NOT NULL CHECK(status IN ('pending','approved','rejected')),
              created_at_utc TEXT NOT NULL,
              reviewed_at_utc TEXT NULL);
            CREATE INDEX ix_memory_candidates_servant_status
              ON memory_candidates(servant_id, status, created_at_utc DESC);
            CREATE TABLE memories(
              memory_id TEXT PRIMARY KEY,
              servant_id TEXT NOT NULL,
              memory_text TEXT NOT NULL CHECK(length(memory_text) <= 2000),
              is_enabled INTEGER NOT NULL CHECK(is_enabled IN (0,1)),
              source_candidate_id TEXT NULL REFERENCES memory_candidates(candidate_id) ON DELETE SET NULL,
              created_at_utc TEXT NOT NULL,
              updated_at_utc TEXT NOT NULL);
            CREATE INDEX ix_memories_servant_enabled
              ON memories(servant_id, is_enabled, updated_at_utc DESC);
            """),
        new(3, """
            ALTER TABLE conversation_summaries
              ADD COLUMN covered_through_message_id TEXT NOT NULL DEFAULT '';
            UPDATE conversation_summaries
            SET covered_through_message_id = COALESCE(
              (SELECT message_id FROM chat_messages
               WHERE chat_messages.conversation_id=conversation_summaries.conversation_id
                 AND chat_messages.sequence=conversation_summaries.covered_through_sequence
               LIMIT 1),
              'legacy-summary');
            """),
        new(4, """
            ALTER TABLE runtime_events ADD COLUMN source TEXT NOT NULL DEFAULT 'system';
            ALTER TABLE runtime_events ADD COLUMN subject_id TEXT NULL;
            ALTER TABLE runtime_events ADD COLUMN summary TEXT NULL;
            ALTER TABLE runtime_events ADD COLUMN is_private INTEGER NOT NULL DEFAULT 0 CHECK(is_private IN (0,1));
            CREATE INDEX ix_runtime_events_source_subject
              ON runtime_events(source, subject_id, occurred_at_utc);
            """),
        new(5, """
            CREATE TABLE todo_items(
              todo_id TEXT PRIMARY KEY,
              title TEXT NOT NULL CHECK(length(title) <= 500),
              description TEXT NULL CHECK(description IS NULL OR length(description) <= 4000),
              priority TEXT NOT NULL CHECK(priority IN ('low','normal','high')),
              due_at_utc TEXT NULL,
              status TEXT NOT NULL CHECK(status IN ('planned','active','completed')),
              created_at_utc TEXT NOT NULL,
              updated_at_utc TEXT NOT NULL,
              completed_at_utc TEXT NULL);
            CREATE INDEX ix_todo_items_status_updated ON todo_items(status, updated_at_utc DESC);
            CREATE INDEX ix_todo_items_completed_date ON todo_items(completed_at_utc);
            CREATE TABLE agent_executions(
              execution_id TEXT PRIMARY KEY,
              todo_id TEXT NOT NULL,
              source_type TEXT NOT NULL,
              source_instance TEXT NOT NULL,
              task_id TEXT NOT NULL,
              dispatch_request_id TEXT NOT NULL UNIQUE,
              status TEXT NOT NULL CHECK(status IN ('dispatching','active','attention','completed','failed','cancelled')),
              started_at_utc TEXT NULL,
              updated_at_utc TEXT NOT NULL,
              ended_at_utc TEXT NULL,
              UNIQUE(source_type, source_instance, task_id));
            CREATE INDEX ix_agent_executions_todo_current
              ON agent_executions(todo_id, updated_at_utc DESC);
            CREATE TABLE agent_event_receipts(
              source_type TEXT NOT NULL,
              source_instance TEXT NOT NULL,
              task_id TEXT NOT NULL,
              sequence INTEGER NOT NULL CHECK(sequence > 0),
              event_type TEXT NOT NULL,
              occurred_at_utc TEXT NOT NULL,
              is_private INTEGER NOT NULL CHECK(is_private IN (0,1)),
              PRIMARY KEY(source_type, source_instance, task_id, sequence));
            CREATE INDEX ix_agent_event_receipts_task
              ON agent_event_receipts(source_type, source_instance, task_id, sequence DESC);
            CREATE TABLE agent_connections(
              source_type TEXT PRIMARY KEY,
              display_name TEXT NOT NULL,
              version TEXT NOT NULL,
              enabled INTEGER NOT NULL CHECK(enabled IN (0,1)),
              last_event_at_utc TEXT NULL,
              pending_count INTEGER NOT NULL CHECK(pending_count >= 0),
              capabilities_json TEXT NOT NULL);
            CREATE TABLE agent_project_targets(
              source_type TEXT NOT NULL,
              target_id TEXT NOT NULL,
              display_name TEXT NOT NULL,
              PRIMARY KEY(source_type, target_id),
              FOREIGN KEY(source_type) REFERENCES agent_connections(source_type) ON DELETE CASCADE);
            CREATE TABLE work_archives(
              archive_id TEXT PRIMARY KEY,
              archive_date TEXT NOT NULL,
              source_types TEXT NOT NULL,
              summary TEXT NOT NULL CHECK(length(summary) <= 6000),
              created_at_utc TEXT NOT NULL);
            CREATE TABLE work_archive_items(
              archive_id TEXT NOT NULL REFERENCES work_archives(archive_id) ON DELETE CASCADE,
              todo_key TEXT NOT NULL,
              PRIMARY KEY(archive_id, todo_key));
            CREATE INDEX ix_work_archives_date ON work_archives(archive_date DESC);
            CREATE TABLE long_work_archives(
              archive_id TEXT PRIMARY KEY,
              title TEXT NOT NULL,
              summary TEXT NOT NULL CHECK(length(summary) <= 6000),
              covered_archive_ids TEXT NOT NULL,
              created_at_utc TEXT NOT NULL);
            CREATE INDEX ix_long_work_archives_created ON long_work_archives(created_at_utc DESC);
            """),
        new(6, """
            ALTER TABLE work_archives ADD COLUMN title TEXT NOT NULL DEFAULT '工作归档';
            ALTER TABLE work_archives ADD COLUMN started_on TEXT NULL;
            ALTER TABLE work_archives ADD COLUMN completed_on TEXT NULL;
            ALTER TABLE work_archives ADD COLUMN outcomes TEXT NOT NULL DEFAULT '[]';
            CREATE TABLE IF NOT EXISTS long_work_archives(
              archive_id TEXT PRIMARY KEY,
              title TEXT NOT NULL,
              summary TEXT NOT NULL CHECK(length(summary) <= 6000),
              covered_archive_ids TEXT NOT NULL,
              created_at_utc TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_long_work_archives_created
              ON long_work_archives(created_at_utc DESC);
            """),
    };

    private readonly RuntimeDatabase _database;

    public RuntimeDatabaseMigrator(RuntimeDatabase database) => _database = database;

    public void Migrate()
    {
        using var connection = _database.Open();
        var current = ReadVersion(connection);
        if (current > Migrations.Count)
        {
            throw new RuntimeDatabaseVersionException(current, Migrations.Count);
        }

        foreach (var migration in Migrations)
        {
            if (migration.Version <= current)
            {
                continue;
            }

            using var transaction = connection.BeginTransaction();
            using (var script = new SqliteCommand(migration.Sql, connection, transaction))
            {
                script.ExecuteNonQuery();
            }

            using (var record = connection.CreateCommand())
            {
                record.Transaction = transaction;
                record.CommandText = "INSERT INTO schema_migrations(version, applied_at_utc) VALUES($version, $applied)";
                record.Parameters.AddWithValue("$version", migration.Version);
                record.Parameters.AddWithValue("$applied", DateTimeOffset.UtcNow.ToString("O"));
                record.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    public static long ReadVersion(SqliteConnection connection)
    {
        using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='schema_migrations'";
        if ((long)exists.ExecuteScalar()! == 0)
        {
            // Fresh file: create the bookkeeping table before any migration runs.
            using var create = connection.CreateCommand();
            create.CommandText = "CREATE TABLE schema_migrations(version INTEGER PRIMARY KEY, applied_at_utc TEXT NOT NULL)";
            create.ExecuteNonQuery();
            return 0;
        }

        using var query = connection.CreateCommand();
        query.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_migrations";
        return (long)query.ExecuteScalar()!;
    }
}

public sealed record Migration(long Version, string Sql);
