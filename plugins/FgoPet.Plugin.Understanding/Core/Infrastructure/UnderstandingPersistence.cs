using System.Text.Json;
using FgoPet.Core.Dialogue;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Understanding.Contracts;
using Microsoft.Data.Sqlite;

namespace FgoPet.Understanding;

public sealed partial class UnderstandingService
{
    public IReadOnlyList<UnderstandingEntry> Changes(ConversationScope scope, string id)
    {
        lock (_gate)
        {
            EnsureSchema(); using var db = database.Open();
            using var cmd = Command(db, null, "SELECT entry_json FROM understanding_revisions WHERE scope=$scope AND entry_id=$id ORDER BY revision DESC LIMIT 20",
                ("$scope", scope.Key), ("$id", id));
            using var r = cmd.ExecuteReader(); var result = new List<UnderstandingEntry>();
            while (r.Read()) result.Add(JsonSerializer.Deserialize<UnderstandingEntry>(r.GetString(0))!);
            return result;
        }
    }
    private void EnsureSchema()
    {
        if (_schemaReady) return;
        new RuntimeDatabaseMigrator(database).MigrateModule("understanding", [new(1, """
            CREATE TABLE understanding_state(scope TEXT PRIMARY KEY, role_id TEXT NOT NULL, project_id TEXT NULL, state_json TEXT NOT NULL);
            CREATE TABLE understanding_entries(scope TEXT NOT NULL, entry_id TEXT NOT NULL, revision INTEGER NOT NULL, entry_json TEXT NOT NULL,
              PRIMARY KEY(scope,entry_id));
            CREATE TABLE understanding_revisions(scope TEXT NOT NULL, entry_id TEXT NOT NULL, revision INTEGER NOT NULL, entry_json TEXT NOT NULL,
              PRIMARY KEY(scope,entry_id,revision));
            CREATE TABLE understanding_receipts(scope TEXT NOT NULL, operation_id TEXT NOT NULL, receipt_json TEXT NOT NULL,
              PRIMARY KEY(scope,operation_id));
            CREATE TABLE understanding_attempts(scope TEXT NOT NULL, attempt_id TEXT NOT NULL, generation INTEGER NOT NULL,
              source_hash TEXT NOT NULL, status TEXT NOT NULL, reserved_calls INTEGER NOT NULL, PRIMARY KEY(scope,attempt_id));
            """), new(2, """
            CREATE TABLE understanding_revocations(scope TEXT NOT NULL, id_hash TEXT NOT NULL, PRIMARY KEY(scope,id_hash));
            """), new(3, """
            CREATE TABLE understanding_coverage(scope TEXT NOT NULL, operation_id TEXT NOT NULL, source_hash TEXT NOT NULL,
              outcomes_json TEXT NOT NULL, PRIMARY KEY(scope,operation_id));
            """), new(4, """
            CREATE TABLE understanding_work_cursors(scope TEXT PRIMARY KEY,cursor_json TEXT NOT NULL);
            CREATE TABLE understanding_work_items(scope TEXT NOT NULL,id TEXT NOT NULL,head_json TEXT NOT NULL,
              scalar_offset INTEGER NOT NULL,state INTEGER NOT NULL,batch_id TEXT NULL,PRIMARY KEY(scope,id));
            CREATE TABLE understanding_work_batches(scope TEXT NOT NULL,id TEXT NOT NULL,batch_json TEXT NOT NULL,
              state INTEGER NOT NULL,outcomes_json TEXT NULL,code TEXT NULL,PRIMARY KEY(scope,id));
            """)]);
        _schemaReady = true;
    }
    private static UnderstandingState ReadState(SqliteConnection db, SqliteTransaction? tx, ConversationScope scope)
    {
        using var cmd = Command(db, tx, "SELECT state_json FROM understanding_state WHERE scope=$scope", ("$scope", scope.Key));
        return cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<UnderstandingState>(json)! : new(new(), 0, 0, 0);
    }
    private static void WriteState(SqliteConnection db, SqliteTransaction tx, ConversationScope scope, UnderstandingState state) =>
        Execute(db, tx, "INSERT INTO understanding_state VALUES($scope,$role,$project,$json) ON CONFLICT(scope) DO UPDATE SET state_json=$json",
            ("$scope", scope.Key), ("$role", scope.ServantId), ("$project", scope.ProjectId), ("$json", JsonSerializer.Serialize(state)));
    private static IReadOnlyList<UnderstandingEntry> ReadEntries(SqliteConnection db, SqliteTransaction? tx, ConversationScope scope)
    {
        using var cmd = Command(db, tx, "SELECT entry_json FROM understanding_entries WHERE scope=$scope", ("$scope", scope.Key));
        using var r = cmd.ExecuteReader(); var entries = new List<UnderstandingEntry>();
        while (r.Read()) entries.Add(JsonSerializer.Deserialize<UnderstandingEntry>(r.GetString(0))!);
        return entries;
    }
    private static void Save(SqliteConnection db, SqliteTransaction tx, UnderstandingEntry entry)
    {
        var json = JsonSerializer.Serialize(entry);
        Execute(db, tx, "INSERT INTO understanding_entries VALUES($scope,$id,$revision,$json) ON CONFLICT(scope,entry_id) DO UPDATE SET revision=$revision,entry_json=$json",
            ("$scope", entry.Scope.Key), ("$id", entry.Id), ("$revision", entry.Revision), ("$json", json));
        Execute(db, tx, "INSERT INTO understanding_revisions VALUES($scope,$id,$revision,$json)",
            ("$scope", entry.Scope.Key), ("$id", entry.Id), ("$revision", entry.Revision), ("$json", json));
    }
    private static UnderstandingReceipt? ReadReceipt(SqliteConnection db, SqliteTransaction? tx, ConversationScope scope, string id)
    {
        using var cmd = Command(db, tx, "SELECT receipt_json FROM understanding_receipts WHERE scope=$scope AND operation_id=$id", ("$scope", scope.Key), ("$id", id));
        return cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<UnderstandingReceipt>(json) : null;
    }
    private static void WriteReceipt(SqliteConnection db, SqliteTransaction tx, ConversationScope scope, UnderstandingReceipt receipt) =>
        Execute(db, tx, "INSERT INTO understanding_receipts VALUES($scope,$id,$json)", ("$scope", scope.Key), ("$id", receipt.OperationId), ("$json", JsonSerializer.Serialize(receipt)));
    // The connection is opened BEFORE owner-port validation. An intervening SQLite commit
    // invalidates the read set; BEGIN IMMEDIATE then blocks source changes through commit.
    // This closes the race without reading Dialogue's private tables from this module.
    private static long DataVersion(SqliteConnection db, SqliteTransaction? tx = null)
    {
        using var command = Command(db, tx, "PRAGMA data_version"); return (long)command.ExecuteScalar()!;
    }
    private static SqliteCommand Command(SqliteConnection db, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] args)
    {
        var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }
    private static void Execute(SqliteConnection db, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] args)
    { using var cmd = Command(db, tx, sql, args); cmd.ExecuteNonQuery(); }
}
