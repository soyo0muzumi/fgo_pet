using System.Text.Json;
using FgoPet.Core.Dialogue;
using FgoPet.Extensibility;
using FgoPet.Understanding.Contracts;

namespace FgoPet.Understanding;

public sealed partial class UnderstandingService
{
    public void DiscardPreviews() { lock (_gate) _previews.Clear(); }
    /// <summary>Metadata-only pre-dispatch reservation. M1 tests substitute the dispatch; no provider is wired here.</summary>
    public bool TryReserveAttempt(ConversationScope scope, string attemptId, IReadOnlyList<ConversationSourceSnapshot> roots, bool manual)
    {
        lock (_gate)
        {
            EnsureSchema(); using var db = database.Open();
            using (var previous = Command(db, null, "SELECT COUNT(*) FROM understanding_attempts WHERE scope=$scope AND attempt_id=$id", ("$scope", scope.Key), ("$id", attemptId)))
                if ((long)previous.ExecuteScalar()! != 0) return false;
            var version = DataVersion(db);
            var ticket = Begin(scope, attemptId, roots, manual);
            using var tx = db.BeginTransaction();
            var state = ReadState(db, tx, scope);
            if (!state.Policy.ExternalProcessing || state.Generation != ticket.Generation || state.Revision != ticket.InputRevision || version != DataVersion(db, tx)) return false;
            if (!manual)
            {
                // New automatic work needs a persistent destination and post-enable evidence.
                // Older authorized sources may accompany that new range as background.
                if (!state.Policy.SaveCandidates || ticket.Sources.All(s => s.SourceRevision <= state.AutoSince)) return false;
                using var processed = Command(db, tx, "SELECT (SELECT COUNT(*) FROM understanding_coverage WHERE scope=$scope AND source_hash=$hash) + (SELECT COUNT(*) FROM understanding_attempts WHERE scope=$scope AND source_hash=$hash)",
                    ("$scope", scope.Key), ("$hash", SourceHash(roots)));
                if ((long)processed.ExecuteScalar()! != 0) return false;
            }
            using var cmd = Command(db, tx, "INSERT OR IGNORE INTO understanding_attempts VALUES($scope,$id,$generation,$hash,'Reserved',1)",
                ("$scope", scope.Key), ("$id", attemptId), ("$generation", state.Generation), ("$hash", SourceHash(roots)));
            var inserted = cmd.ExecuteNonQuery() == 1; tx.Commit();
            Log(attemptId, inserted ? "attempt_reserved" : "attempt_duplicate"); return inserted;
        }
    }
    private static string SourceHash(IReadOnlyList<ConversationSourceSnapshot> roots) => HashId(JsonSerializer.Serialize(
        roots.OrderBy(s => s.Scope.ConversationId, StringComparer.Ordinal).ThenBy(s => s.MessageId, StringComparer.Ordinal)));
    public IReadOnlyList<UnderstandingItemReceipt> Coverage(ConversationScope scope, IReadOnlyList<ConversationSourceSnapshot> roots)
    {
        lock (_gate)
        {
            EnsureSchema(); using var db = database.Open();
            using var command = Command(db, null, "SELECT outcomes_json FROM understanding_coverage WHERE scope=$scope AND source_hash=$hash ORDER BY rowid DESC LIMIT 1",
                ("$scope", scope.Key), ("$hash", SourceHash(roots)));
            return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<UnderstandingItemReceipt[]>(json)! : [];
        }
    }
    public void CompleteAttempt(ConversationScope scope, string attemptId, UnderstandingOutcome outcome)
    {
        if (!Enum.IsDefined(outcome)) throw new ArgumentException("UNDERSTANDING_INVALID_OUTCOME");
        lock (_gate)
        {
            EnsureSchema(); using var db = database.Open();
            Execute(db, null, "UPDATE understanding_attempts SET status=$status WHERE scope=$scope AND attempt_id=$id AND status='Reserved'",
                ("$scope", scope.Key), ("$id", attemptId), ("$status", outcome.ToString()));
            Log(attemptId, "attempt_completed");
        }
    }
    public void StartSession()
    {
        lock (_gate)
        {
            EnsureSchema(); using var db = database.Open();
            Execute(db, null, "UPDATE understanding_attempts SET status='Interrupted' WHERE status='Reserved'");
            _previews.Clear(); Log("session", "interrupted_attempts_closed");
        }
    }
    public IReadOnlyList<(string Status, int ReservedCalls)> AttemptStates(ConversationScope scope)
    {
        lock (_gate)
        {
            EnsureSchema(); using var db = database.Open();
            using var cmd = Command(db, null, "SELECT status,reserved_calls FROM understanding_attempts WHERE scope=$scope ORDER BY attempt_id", ("$scope", scope.Key));
            using var reader = cmd.ExecuteReader(); var result = new List<(string, int)>();
            while (reader.Read()) result.Add((reader.GetString(0), reader.GetInt32(1)));
            return result;
        }
    }
}
