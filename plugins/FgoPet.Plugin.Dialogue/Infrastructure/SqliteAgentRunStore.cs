using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Kernel.Agent;
using FgoPet.Platform.Secrets;
using Microsoft.Data.Sqlite;

namespace FgoPet.Infrastructure.Dialogue;

/// <summary>Conversation-owned protected execution state. Journal and checkpoint publish in one transaction.</summary>
public sealed class SqliteAgentRunStore : IAgentRunStore
{
    private const int MaxCheckpointBytes = 4 * 1024 * 1024;
    private const long MaxProtectedBytes = 64 * 1024 * 1024;
    private const int MaxRuns = 256;
    private const int MaxJournal = 1024;
    private readonly RuntimeDatabase _database;
    private readonly IProtectedStateProtector _protector;
    private readonly TimeProvider _time;
    private readonly object _schemaGate = new();
    private bool _schemaReady;
    private static readonly JsonSerializerOptions Json = new() {
        MaxDepth = 48, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private static readonly Migration[] Migrations = [new(1, """
        CREATE TABLE agent_runs(
          run_key TEXT PRIMARY KEY,
          conversation_id TEXT NOT NULL REFERENCES conversations(conversation_id) ON DELETE CASCADE,
          root_message_id TEXT NOT NULL REFERENCES chat_messages(message_id) ON DELETE CASCADE,
          role_id TEXT NOT NULL, project_id TEXT NULL,
          revision INTEGER NOT NULL CHECK(revision>=0), journal_sequence INTEGER NOT NULL CHECK(journal_sequence>0),
          status INTEGER NOT NULL, completed_at_utc TEXT NULL, checkpoint BLOB NOT NULL);
        CREATE INDEX ix_agent_runs_conversation ON agent_runs(conversation_id);
        CREATE UNIQUE INDEX ux_agent_runs_active_conversation ON agent_runs(conversation_id) WHERE completed_at_utc IS NULL;
        CREATE TABLE agent_run_events(
          run_key TEXT NOT NULL REFERENCES agent_runs(run_key) ON DELETE CASCADE,
          sequence INTEGER NOT NULL CHECK(sequence>0), timestamp_utc TEXT NOT NULL,
          kind INTEGER NOT NULL, run_correlation TEXT NOT NULL, step_number INTEGER NOT NULL,
          call_correlation TEXT NULL, error_code TEXT NULL, PRIMARY KEY(run_key,sequence));
        """)];

    public SqliteAgentRunStore(RuntimeDatabase database, IProtectedStateProtector protector, TimeProvider? time = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
        _time = time ?? TimeProvider.System;
    }

    public ValueTask<AgentRunCheckpoint?> LoadAsync(string runId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Safe(() => {
            using var connection = _database.Open();
            return Read(connection, null, Key(runId));
        });
    }

    public ValueTask<bool> TryCreateAsync(AgentRunCheckpoint initial, ImmutableArray<AgentEvent> events, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Safe(() => {
            Validate(initial);
            if (initial.Snapshot.Revision != 0 || initial.Snapshot.Status != AgentRunStatus.Created ||
                !ValidEvents(initial, 0, events)) return false;
            var key = Key(initial.Snapshot.Identity.RunId);
            var encrypted = Encode(initial, key);
            using var connection = _database.Open();
            using var transaction = connection.BeginTransaction();
            if (!ScopeExists(connection, transaction, initial.Snapshot.Identity) || Read(connection, transaction, key) is not null)
                return false;
            using (var active = new SqliteCommand("SELECT COUNT(*) FROM agent_runs WHERE conversation_id=$conversation AND completed_at_utc IS NULL", connection, transaction))
            {
                active.Parameters.AddWithValue("$conversation", initial.Snapshot.Identity.Scope.ConversationId);
                if ((long)active.ExecuteScalar()! != 0) return false;
            }
            Prune(connection, transaction);
            EnsureQuota(connection, transaction, encrypted.Length, 0, creating: true);
            using var insert = new SqliteCommand("""
                INSERT INTO agent_runs VALUES($key,$conversation,$root,$role,$project,$revision,$journal,$status,$completed,$checkpoint)
                """, connection, transaction);
            Bind(insert, initial, key, encrypted);
            insert.ExecuteNonQuery();
            InsertEvents(connection, transaction, key, events);
            token.ThrowIfCancellationRequested();
            transaction.Commit();
            return true;
        });
    }

    public ValueTask<bool> TryCommitAsync(AgentRunCheckpoint next, long expectedRevision,
        ImmutableArray<AgentEvent> events, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Safe(() => {
            Validate(next);
            if (expectedRevision < 0 || expectedRevision == long.MaxValue || next.Snapshot.Revision != expectedRevision + 1)
                return false;
            var key = Key(next.Snapshot.Identity.RunId);
            var encrypted = Encode(next, key);
            using var connection = _database.Open();
            using var transaction = connection.BeginTransaction();
            var current = Read(connection, transaction, key);
            if (current is null || current.Snapshot.Revision != expectedRevision || current.Snapshot.IsTerminal ||
                current.Snapshot.Identity != next.Snapshot.Identity || current.Snapshot.Budget != next.Snapshot.Budget ||
                next.Snapshot.Status is not (AgentRunStatus.Interrupted or AgentRunStatus.ExecutionUnknown or AgentRunStatus.Cancelled) &&
                    !ScopeExists(connection, transaction, next.Snapshot.Identity) || !ValidEvents(next, current.JournalSequence, events) ||
                !Monotonic(current, next)) return false;
            using var size = new SqliteCommand("SELECT length(checkpoint) FROM agent_runs WHERE run_key=$key", connection, transaction);
            size.Parameters.AddWithValue("$key", key);
            EnsureQuota(connection, transaction, encrypted.Length, (long)size.ExecuteScalar()!, creating: false);
            using var update = new SqliteCommand("""
                UPDATE agent_runs SET revision=$revision,journal_sequence=$journal,status=$status,
                  completed_at_utc=$completed,checkpoint=$checkpoint
                WHERE run_key=$key AND revision=$expected
                """, connection, transaction);
            Bind(update, next, key, encrypted);
            update.Parameters.AddWithValue("$expected", expectedRevision);
            if (update.ExecuteNonQuery() != 1) return false;
            InsertEvents(connection, transaction, key, events);
            token.ThrowIfCancellationRequested();
            transaction.Commit();
            return true;
        });
    }

    /// <summary>Call once before host admission. No model/tool ports are used and waiting authorization is discarded.</summary>
    public async ValueTask<int> CloseInterruptedRunsAsync(CancellationToken token)
    {
        var pending = await Safe(() => {
            using var connection = _database.Open();
            using var query = connection.CreateCommand();
            query.CommandText = "SELECT run_key FROM agent_runs WHERE completed_at_utc IS NULL LIMIT 257";
            using var reader = query.ExecuteReader();
            var keys = new List<string>();
            while (reader.Read()) keys.Add(reader.GetString(0));
            if (keys.Count > MaxRuns) throw new AgentStateException("RUN_STATE_QUOTA_EXCEEDED");
            return keys;
        });
        var count = 0;
        foreach (var key in pending)
        {
            token.ThrowIfCancellationRequested();
            var current = await Safe(() => { using var connection = _database.Open(); return Read(connection, null, key); });
            if (current is null || current.Snapshot.IsTerminal) continue;
            var unknown = current.Calls.Any(call => call.Status == AgentCallStatus.ExecutionUnknown ||
                call.Status == AgentCallStatus.Started && call.Effect == ToolEffect.Command);
            var status = unknown ? AgentRunStatus.ExecutionUnknown : AgentRunStatus.Interrupted;
            var code = unknown ? "RUN_EXECUTION_UNKNOWN" : "RUN_INTERRUPTED";
            var next = current with {
                Snapshot = current.Snapshot with { Revision = current.Snapshot.Revision + 1, Status = status,
                    CompletedAt = _time.GetUtcNow(), ErrorCode = code },
                Waiting = null, PendingApproval = null, PendingInput = null,
                Calls = current.Calls.Select(call => call.Status == AgentCallStatus.Requested
                    ? call with { Status = AgentCallStatus.NotExecuted } : call.Status == AgentCallStatus.Started
                    ? call with { Status = call.Effect == ToolEffect.Command ? AgentCallStatus.ExecutionUnknown : AgentCallStatus.NotExecuted }
                    : call).ToImmutableArray(), JournalSequence = current.JournalSequence + 1 };
            var item = new AgentEvent(next.JournalSequence, _time.GetUtcNow(), unknown ? AgentEventKind.RunExecutionUnknown
                : AgentEventKind.RunInterrupted, Correlation(current.Snapshot.Identity.RunId), current.Snapshot.StepNumber, ErrorCode: code);
            if (await TryCommitAsync(next, current.Snapshot.Revision, [item], token)) count++;
        }
        return count;
    }

    private AgentRunCheckpoint? Read(SqliteConnection connection, SqliteTransaction? transaction, string key)
    {
        using var query = new SqliteCommand("SELECT conversation_id,root_message_id,role_id,project_id,revision,journal_sequence,status,completed_at_utc,checkpoint FROM agent_runs WHERE run_key=$key", connection, transaction);
        query.Parameters.AddWithValue("$key", key);
        using var reader = query.ExecuteReader();
        if (!reader.Read()) return null;
        if (reader.GetBytes(8, 0, null, 0, 0) > MaxCheckpointBytes + 65536) throw new AgentStateException("RUN_STATE_UNREADABLE");
        var plaintext = _protector.Unprotect((byte[])reader.GetValue(8), Purpose(key));
        try
        {
            if (plaintext.Length is < 1 or > MaxCheckpointBytes) throw new AgentStateException("RUN_STATE_UNREADABLE");
            var state = JsonSerializer.Deserialize<AgentRunCheckpoint>(plaintext, Json) ?? throw new AgentStateException("RUN_STATE_UNREADABLE");
            Validate(state);
            var identity = state.Snapshot.Identity;
            if (Key(identity.RunId) != key || identity.Scope.ConversationId != reader.GetString(0) ||
                identity.RootUserMessageId != reader.GetString(1) || identity.Scope.RoleId != reader.GetString(2) ||
                identity.Scope.ProjectId != (reader.IsDBNull(3) ? null : reader.GetString(3)) ||
                state.Snapshot.Revision != reader.GetInt64(4) || state.JournalSequence != reader.GetInt64(5) ||
                (int)state.Snapshot.Status != reader.GetInt32(6) || state.Snapshot.CompletedAt?.ToString("O") !=
                (reader.IsDBNull(7) ? null : reader.GetString(7))) throw new AgentStateException("RUN_STATE_UNREADABLE");
            return state;
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private byte[] Encode(AgentRunCheckpoint state, string key)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(state, Json);
        try {
            if (plaintext.Length > MaxCheckpointBytes) throw new AgentStateException("RUN_STATE_QUOTA_EXCEEDED");
            var encrypted = _protector.Protect(plaintext, Purpose(key));
            if (encrypted.Length is < 1 or > MaxCheckpointBytes + 65536) throw new AgentStateException("RUN_STATE_UNREADABLE");
            return encrypted;
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private static void Bind(SqliteCommand command, AgentRunCheckpoint state, string key, byte[] encrypted)
    {
        var snapshot = state.Snapshot;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$conversation", snapshot.Identity.Scope.ConversationId);
        command.Parameters.AddWithValue("$root", snapshot.Identity.RootUserMessageId);
        command.Parameters.AddWithValue("$role", snapshot.Identity.Scope.RoleId);
        command.Parameters.AddWithValue("$project", (object?)snapshot.Identity.Scope.ProjectId ?? DBNull.Value);
        command.Parameters.AddWithValue("$revision", snapshot.Revision);
        command.Parameters.AddWithValue("$journal", state.JournalSequence);
        command.Parameters.AddWithValue("$status", (int)snapshot.Status);
        command.Parameters.AddWithValue("$completed", (object?)snapshot.CompletedAt?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$checkpoint", encrypted);
    }

    private static bool ScopeExists(SqliteConnection connection, SqliteTransaction transaction, AgentRunIdentity identity)
    {
        using var query = new SqliteCommand("""
            SELECT COUNT(*) FROM conversations c JOIN chat_messages m ON m.conversation_id=c.conversation_id
            WHERE c.conversation_id=$conversation AND c.servant_id=$role AND c.project_id IS $project
              AND c.status='active' AND m.message_id=$root AND m.servant_id=$role AND m.role='user' AND m.status='completed'
            """, connection, transaction);
        query.Parameters.AddWithValue("$conversation", identity.Scope.ConversationId);
        query.Parameters.AddWithValue("$role", identity.Scope.RoleId);
        query.Parameters.AddWithValue("$project", (object?)identity.Scope.ProjectId ?? DBNull.Value);
        query.Parameters.AddWithValue("$root", identity.RootUserMessageId);
        return (long)query.ExecuteScalar()! == 1;
    }

    private void Prune(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var probe = new SqliteCommand("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='agent_final_deliveries'", connection, transaction);
        var hasDeliveries = (long)probe.ExecuteScalar()! != 0;
        // An accepted final retains its source Run. Retention must not cascade away a delivery ledger entry.
        var protectedFinal = hasDeliveries
            ? " AND NOT EXISTS (SELECT 1 FROM agent_final_deliveries d WHERE d.run_key=agent_runs.run_key)" : string.Empty;
        using var delete = new SqliteCommand("DELETE FROM agent_runs WHERE completed_at_utc IS NOT NULL AND completed_at_utc<$cutoff" + protectedFinal, connection, transaction);
        delete.Parameters.AddWithValue("$cutoff", (_time.GetUtcNow() - TimeSpan.FromDays(7)).ToString("O"));
        delete.ExecuteNonQuery();
    }

    private static void EnsureQuota(SqliteConnection connection, SqliteTransaction transaction, int nextSize, long oldSize, bool creating)
    {
        using var query = new SqliteCommand("SELECT COUNT(*),COALESCE(SUM(length(checkpoint)),0) FROM agent_runs", connection, transaction);
        using var reader = query.ExecuteReader();
        reader.Read();
        if (reader.GetInt64(0) + (creating ? 1 : 0) > MaxRuns || reader.GetInt64(1) - oldSize + nextSize > MaxProtectedBytes)
            throw new AgentStateException("RUN_STATE_QUOTA_EXCEEDED");
    }

    private static void InsertEvents(SqliteConnection connection, SqliteTransaction transaction, string key, ImmutableArray<AgentEvent> events)
    {
        foreach (var item in events)
        {
            using var insert = new SqliteCommand("INSERT INTO agent_run_events VALUES($key,$sequence,$time,$kind,$run,$step,$call,$error)", connection, transaction);
            insert.Parameters.AddWithValue("$key", key);
            insert.Parameters.AddWithValue("$sequence", item.Sequence);
            insert.Parameters.AddWithValue("$time", item.Timestamp.ToString("O"));
            insert.Parameters.AddWithValue("$kind", (int)item.Kind);
            insert.Parameters.AddWithValue("$run", item.RunCorrelation);
            insert.Parameters.AddWithValue("$step", item.StepNumber);
            insert.Parameters.AddWithValue("$call", (object?)item.CallCorrelation ?? DBNull.Value);
            insert.Parameters.AddWithValue("$error", (object?)item.ErrorCode ?? DBNull.Value);
            insert.ExecuteNonQuery();
        }
    }

    private static bool ValidEvents(AgentRunCheckpoint state, long previous, ImmutableArray<AgentEvent> events) =>
        !events.IsDefaultOrEmpty && state.JournalSequence == previous + events.Length &&
        state.JournalSequence <= (state.Snapshot.IsTerminal ? MaxJournal : MaxJournal - 1) &&
        events.Select((item, index) => item.Sequence == previous + index + 1 && Enum.IsDefined(item.Kind) &&
            item.Timestamp.Offset == TimeSpan.Zero && item.StepNumber >= 0 &&
            item.RunCorrelation == Correlation(state.Snapshot.Identity.RunId) &&
            (item.CallCorrelation is null || Hash(item.CallCorrelation, 24)) && SafeCode(item.ErrorCode)).All(value => value);

    private static bool Monotonic(AgentRunCheckpoint current, AgentRunCheckpoint next) =>
        next.Snapshot.StartedAt == current.Snapshot.StartedAt && next.Snapshot.ModelRequests >= current.Snapshot.ModelRequests &&
        next.Snapshot.ToolCalls >= current.Snapshot.ToolCalls && next.Snapshot.LoadedSkills >= current.Snapshot.LoadedSkills &&
        next.Snapshot.StepNumber >= current.Snapshot.StepNumber &&
        !(next.Snapshot.Status == AgentRunStatus.Created && current.Snapshot.Status != AgentRunStatus.Created);

    private static void Validate(AgentRunCheckpoint state)
    {
        var s = state.Snapshot;
        var id = s?.Identity;
        if (state.SchemaVersion != 1 || s is null || id?.Scope is null || s.Budget is null || !Id(id.RunId) ||
            !Id(id.RootUserMessageId) || !Id(id.ModelRevision) || !Id(id.Scope.ConversationId) || !Id(id.Scope.RoleId) ||
            id.Scope.ProjectId is not null && !Id(id.Scope.ProjectId) || id.AuthorizationRevision < 0 ||
            !Enum.IsDefined(s.Status) || s.Revision < 0 || state.JournalSequence is < 1 or > MaxJournal ||
            s.ModelRequests < 0 || s.ModelRequests > s.Budget.MaxModelRequests || s.ToolCalls < 0 || s.ToolCalls > s.Budget.MaxToolCalls ||
            s.LoadedSkills < 0 || s.LoadedSkills > s.Budget.MaxLoadedSkills || s.StepNumber < 0 ||
            s.StartedAt.Offset != TimeSpan.Zero || s.IsTerminal != (s.CompletedAt is not null) || !SafeCode(s.ErrorCode) ||
            state.Calls.IsDefault || state.Calls.Length > 16 || state.NextCallIndex < 0 || state.NextCallIndex > state.Calls.Length ||
            state.CompletedSteps.IsDefault || state.CompletedSteps.Length > MaxJournal ||
            state.ActiveSkills.IsDefault || state.ActiveSkills.Length != s.LoadedSkills || state.ResolvedInputs.IsDefault ||
            state.ResolvedInputs.Length > s.Budget.MaxToolCalls || state.FinalText?.Length > 12000)
            throw new AgentStateException("RUN_STATE_UNREADABLE");
        var all = state.CompletedSteps.SelectMany(step => step.Calls).Concat(state.Calls).ToArray();
        if (state.CompletedSteps.Select((step, index) => step is null || step.StepNumber < 1 || step.StepNumber >= s.StepNumber ||
                index > 0 && step.StepNumber <= state.CompletedSteps[index - 1].StepNumber || step.Calls.IsDefaultOrEmpty || step.Calls.Length > 16).Any(bad => bad) ||
            all.Length > (long)s.Budget.MaxToolCalls + 16 || all.Any(call => call is null || call.Call is null ||
            !Id(call.Call.CallId) || !Id(call.Call.Name) || call.Call.ArgumentsJson is null || Encoding.UTF8.GetByteCount(call.Call.ArgumentsJson) > 65536 ||
            !Enum.IsDefined(call.Status) || call.Effect is not null && !Enum.IsDefined(call.Effect.Value) ||
            call.Result is not null && (call.Result.Payload.ValueKind == JsonValueKind.Undefined ||
                Encoding.UTF8.GetByteCount(call.Result.Payload.GetRawText()) > 65536 || !SafeCode(call.Result.ErrorCode, 128)) ||
            call.Result?.Conversation is not null || call.Result?.ExecutionState is not null && !Enum.IsDefined(call.Result.ExecutionState.Value)) ||
            all.Count(call => call.Charged) != s.ToolCalls || all.Select(call => call.Call.CallId).Distinct(StringComparer.Ordinal).Count() != all.Length ||
            state.ActiveSkills.Any(skill => skill is null || !Id(skill.Id) || !Id(skill.PluginId) || !Id(skill.Version) || !Hash(skill.ContentDigest, 64)))
            throw new AgentStateException("RUN_STATE_UNREADABLE");
        if (state.Waiting is not null && (s.Status is not (AgentRunStatus.WaitingApproval or AgentRunStatus.WaitingUserInput) ||
            state.NextCallIndex >= state.Calls.Length || state.Waiting.Revision != s.Revision ||
            state.Waiting.CallId != state.Calls[state.NextCallIndex].Call.CallId || state.Waiting.StepNumber != s.StepNumber ||
            !Enum.IsDefined(state.Waiting.Kind) || !Id(state.Waiting.RequestId) || state.Waiting.ExpiresAt.Offset != TimeSpan.Zero ||
            state.Calls[state.NextCallIndex].Status != AgentCallStatus.Requested || !state.Calls[state.NextCallIndex].Charged ||
            (state.Waiting.Kind == AgentWaitKind.Approval) != (s.Status == AgentRunStatus.WaitingApproval)))
            throw new AgentStateException("RUN_STATE_UNREADABLE");
        if (s.IsTerminal && (state.Waiting is not null || state.PendingApproval is not null || state.PendingInput is not null))
            throw new AgentStateException("RUN_STATE_UNREADABLE");
        if (s.Status is AgentRunStatus.WaitingApproval or AgentRunStatus.WaitingUserInput && state.Waiting is null ||
            state.Waiting is null && (state.PendingApproval is not null || state.PendingInput is not null) ||
            s.Status == AgentRunStatus.Completed && (string.IsNullOrWhiteSpace(state.FinalText) || !Id(state.FinalDeliveryId)) ||
            s.CompletedAt is not null && (s.CompletedAt.Value.Offset != TimeSpan.Zero || s.CompletedAt < s.StartedAt))
            throw new AgentStateException("RUN_STATE_UNREADABLE");
        if (state.Waiting is not null && (state.Waiting.Kind == AgentWaitKind.Approval
            ? state.PendingApproval is null || state.PendingInput is not null || !WaitBinding(state, state.PendingApproval.Binding)
            : state.PendingInput is null || state.PendingApproval is not null || !WaitBinding(state, state.PendingInput.Binding)))
            throw new AgentStateException("RUN_STATE_UNREADABLE");
    }

    private static bool WaitBinding(AgentRunCheckpoint state, InteractionBinding? binding) => binding is not null &&
        binding.Identity == state.Snapshot.Identity && binding.StepNumber == state.Waiting!.StepNumber &&
        binding.CallId == state.Waiting.CallId && binding.RequestId == state.Waiting.RequestId &&
        binding.WaitingRevision == state.Waiting.Revision && binding.ExpiresAt == state.Waiting.ExpiresAt;

    private static bool Id(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128 && !value.Any(char.IsControl);
    private static bool Hash(string value, int length) => value.Length == length && value.All(c => c is >= 'A' and <= 'F' or >= '0' and <= '9');
    private static bool SafeCode(string? value, int maximum = 64) => value is null || value.Length > 0 && value.Length <= maximum &&
        value[0] is >= 'A' and <= 'Z' && value.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_');
    private static string Key(string runId) { if (!Id(runId)) throw new AgentStateException("RUN_INVALID_SCOPE"); return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(runId))); }
    private static string Correlation(string runId) => Key(runId)[..24];
    private static string Purpose(string key) => "native-run." + key.ToLowerInvariant() + ".v1";
    private ValueTask<T> Safe<T>(Func<T> operation)
    {
        try
        {
            lock (_schemaGate)
            {
                if (!_schemaReady)
                {
                    new RuntimeDatabaseMigrator(_database).MigrateModule("native-agent", Migrations);
                    _schemaReady = true;
                }
            }
            return ValueTask.FromResult(operation());
        }
        catch (AgentStateException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (StateProtectionException) { throw new AgentStateException("RUN_STATE_UNREADABLE"); }
        catch (JsonException) { throw new AgentStateException("RUN_STATE_UNREADABLE"); }
        catch (Exception) { throw new AgentStateException("RUN_STORE_FAILED"); }
    }
}
