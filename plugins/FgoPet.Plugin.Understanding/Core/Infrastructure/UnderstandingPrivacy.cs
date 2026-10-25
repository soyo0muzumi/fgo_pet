using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FgoPet.Core.Dialogue;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Understanding.Contracts;
using Microsoft.Data.Sqlite;

namespace FgoPet.Understanding;

public sealed partial class UnderstandingService
{
    private static string HashId(string id) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)));
    private static bool IsRevoked(SqliteConnection db, SqliteTransaction tx, string scope, string id)
    {
        using var cmd = Command(db, tx, "SELECT COUNT(*) FROM understanding_revocations WHERE scope=$scope AND id_hash=$hash",
            ("$scope", scope), ("$hash", HashId(id)));
        return (long)cmd.ExecuteScalar()! != 0;
    }
    private static void Revoke(SqliteConnection db, SqliteTransaction tx, string scope, string id) =>
        Execute(db, tx, "INSERT OR IGNORE INTO understanding_revocations VALUES($scope,$hash)", ("$scope", scope), ("$hash", HashId(id)));
    private static IReadOnlyList<ConversationScope> Scopes(SqliteConnection db, SqliteTransaction? tx)
    {
        using var cmd = Command(db, tx, "SELECT role_id,project_id FROM understanding_state"); using var r = cmd.ExecuteReader();
        var items = new List<ConversationScope>();
        while (r.Read()) items.Add(new(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1)));
        return items;
    }
    public void ForgetConversation(string conversationId, string roleId) => Forget(roleId, conversationId);
    public void ForgetAll() => Forget(null, null);
    private void Forget(string? roleId, string? conversationId)
    {
        lock (_gate)
        {
            EnsureSchema(); using var db = database.Open(); using var tx = db.BeginTransaction();
            foreach (var scope in Scopes(db, tx).Where(s => roleId is null || s.ServantId == roleId))
            {
                // Remove every revision derived from a forgotten root, including now-replaced entries.
                using var query = Command(db, tx, "SELECT entry_json FROM understanding_revisions WHERE scope=$scope", ("$scope", scope.Key));
                var ids = new HashSet<string>(StringComparer.Ordinal);
                using (var reader = query.ExecuteReader())
                    while (reader.Read())
                    {
                        var entry = JsonSerializer.Deserialize<UnderstandingEntry>(reader.GetString(0))!;
                        if (conversationId is null || entry.Sources.Any(s => s.Scope.ConversationId == conversationId)) ids.Add(entry.Id);
                    }
                foreach (var id in ids)
                {
                    Revoke(db, tx, scope.Key, id);
                    Execute(db, tx, "DELETE FROM understanding_entries WHERE scope=$scope AND entry_id=$id", ("$scope", scope.Key), ("$id", id));
                    Execute(db, tx, "DELETE FROM understanding_revisions WHERE scope=$scope AND entry_id=$id", ("$scope", scope.Key), ("$id", id));
                }
                var state = ReadState(db, tx, scope);
                WriteState(db, tx, scope, state with { Revision = state.Revision + 1, Generation = state.Generation + 1,
                    Policy = conversationId is null ? new() : state.Policy });
                // These contain no bodies. Forgetting also removes their source hashes and receipts.
                Execute(db, tx, "DELETE FROM understanding_receipts WHERE scope=$scope", ("$scope", scope.Key));
                Execute(db, tx, "DELETE FROM understanding_attempts WHERE scope=$scope", ("$scope", scope.Key));
                Execute(db, tx, "DELETE FROM understanding_coverage WHERE scope=$scope", ("$scope", scope.Key));
                if(conversationId is null)
                {
                    Execute(db,tx,"DELETE FROM understanding_work_batches WHERE scope=$scope",("$scope",scope.Key));
                    Execute(db,tx,"DELETE FROM understanding_work_items WHERE scope=$scope",("$scope",scope.Key));
                }
                else
                {
                    Execute(db,tx,"DELETE FROM understanding_work_batches WHERE scope=$scope AND id IN (SELECT b.id FROM understanding_work_batches b,json_each(b.batch_json,'$.Items') i WHERE b.scope=$scope AND json_extract(i.value,'$.Head.Source.Scope.ConversationId')=$conversation)",
                        ("$scope",scope.Key),("$conversation",conversationId));
                    Execute(db,tx,"DELETE FROM understanding_work_items WHERE scope=$scope AND json_extract(head_json,'$.Source.Scope.ConversationId')=$conversation",("$scope",scope.Key),("$conversation",conversationId));
                    Execute(db,tx,"UPDATE understanding_work_items SET state=0,batch_id=NULL WHERE scope=$scope AND state=1 AND batch_id NOT IN (SELECT id FROM understanding_work_batches WHERE scope=$scope)",("$scope",scope.Key));
                }
                // Keep the cursor: forgetting must not rediscover an old original as a fresh automatic interval.
            }
            tx.Commit();
            foreach (var key in _previews.Where(p => roleId is null || p.Value.Scope.ServantId == roleId).Select(p => p.Key).ToArray()) _previews.Remove(key);
            Log(roleId ?? "all", "forgotten");
        }
    }
    public JsonElement Export()
    {
        lock (_gate)
        {
            EnsureSchema(); using var db = database.Open(); using var tx = db.BeginTransaction(deferred: true);
            var entries = Scopes(db, tx).SelectMany(s => ReadEntries(db, tx, s)).ToArray();
            // Explicit user export; no in-memory previews, credentials or provider requests.
            return JsonSerializer.SerializeToElement(new { Version = 1, Entries = entries });
        }
    }
    public void NormalizeRestoredCopy(string validatedStagingDatabasePath)
    {
        var fullPath = Path.GetFullPath(validatedStagingDatabasePath);
        if (string.Equals(fullPath, database.DatabasePath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("UNDERSTANDING_RESTORE_REQUIRES_STAGING");
        lock (_gate)
        {
            EnsureSchema(); using var current = database.Open();
            var revoked = new List<(string Scope, string Hash)>();
            using (var cmd = Command(current, null, "SELECT scope,id_hash FROM understanding_revocations"))
            using (var r = cmd.ExecuteReader()) while (r.Read()) revoked.Add((r.GetString(0), r.GetString(1)));
            var currentStates = Scopes(current, null).ToDictionary(s => s.Key, s => ReadState(current, null, s));
            var currentEntries = Scopes(current, null).SelectMany(s => ReadEntries(current, null, s))
                .ToDictionary(e => (e.Scope.Key, e.Id));
            var stagedDatabase = new RuntimeDatabase(fullPath, pooling: false);
            var staged = new UnderstandingService(stagedDatabase, sources, _clock);
            staged.EnsureSchema();
            using var db = stagedDatabase.Open(); using var tx = db.BeginTransaction();
            foreach (var (scope, hash) in revoked)
                Execute(db, tx, "INSERT OR IGNORE INTO understanding_revocations VALUES($scope,$hash)", ("$scope", scope), ("$hash", hash));
            foreach (var scope in Scopes(db, tx))
            {
                var state = ReadState(db, tx, scope);
                var currentState = currentStates.GetValueOrDefault(scope.Key);
                var generation = Math.Max(state.Generation, currentState?.Generation ?? 0) + 1;
                WriteState(db, tx, scope, state with { Revision = Math.Max(state.Revision, currentState?.Revision ?? 0) + 1, Generation = generation, Policy = new() });
                foreach (var entry in ReadEntries(db, tx, scope))
                {
                    if (IsRevoked(db, tx, scope.Key, entry.Id))
                    {
                        Execute(db, tx, "DELETE FROM understanding_entries WHERE scope=$scope AND entry_id=$id", ("$scope", scope.Key), ("$id", entry.Id));
                        Execute(db, tx, "DELETE FROM understanding_revisions WHERE scope=$scope AND entry_id=$id", ("$scope", scope.Key), ("$id", entry.Id));
                    }
                    else
                    {
                        var currentEntry = currentEntries.GetValueOrDefault((scope.Key, entry.Id));
                        // Restoring an older copy must not erase a later terminal state.
                        var life = entry.Life == UnderstandingLife.Retracted || currentEntry?.Life == UnderstandingLife.Retracted ? UnderstandingLife.Retracted :
                            entry.Life == UnderstandingLife.Resolved || currentEntry?.Life == UnderstandingLife.Resolved ? UnderstandingLife.Resolved : UnderstandingLife.Dormant;
                        Save(db, tx, entry with { Revision = Math.Max(entry.Revision, currentEntry?.Revision ?? 0) + 1,
                            Life = life, PolicyGeneration = generation });
                    }
                }
            }
            // Interrupted metadata never schedules a retry on restore/restart.
            Execute(db, tx, "UPDATE understanding_attempts SET status='Interrupted' WHERE status='Reserved'");
            Execute(db, tx, "UPDATE understanding_work_batches SET state=7 WHERE state IN (0,1)");
            Execute(db, tx, "UPDATE understanding_work_items SET state=7,batch_id=NULL WHERE state IN (0,1)");
            tx.Commit(); Log("restore", "staged_copy_normalized");
        }
    }
}
