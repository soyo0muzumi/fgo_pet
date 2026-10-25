// Non-production contracts. Linked only into the offline App test project.
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FgoPet.Core.Dialogue;
using FgoPet.Kernel.Agent;
using FgoPet.Understanding.Contracts;
using Microsoft.Data.Sqlite;

namespace FgoPet.Validation.Understanding;

internal sealed record ReviewDispatchContext(string ScopeHash, string RouteHash, long PolicyGeneration);
internal sealed record ReviewDispatchBinding(string BatchHash, string TrialHash, string RequestHash,
    string OperationHash, string InputHash, ReviewDispatchContext Context);
internal sealed record ReviewDispatchRecord(string RequestHash, string OperationHash, string State, int? InputTokens, int? OutputTokens);

internal sealed class ReviewDispatchLedgerProbe
{
    private readonly string _path;

    public ReviewDispatchLedgerProbe(string path, string taskHash, int totalLimit)
    {
        Digest(taskHash); Limit(totalLimit);
        _path = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(_path)!;
        // Only dedicated, disposable fixtures may be opened. Never accept a runtime database.
        if (!Path.IsPathFullyQualified(path) || Path.GetFileName(_path) != "dispatch-probe.db" ||
            !Path.GetFileName(directory).StartsWith("fgopet-review-dispatch-", StringComparison.Ordinal) ||
            !string.Equals(Path.GetDirectoryName(directory), Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw Error("REVIEW_DISPATCH_ISOLATION_REQUIRED");
        Directory.CreateDirectory(directory);
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 ||
            (File.Exists(_path) && (File.GetAttributes(_path) & FileAttributes.ReparsePoint) != 0))
            throw Error("REVIEW_DISPATCH_ISOLATION_REQUIRED");
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        Execute(db, tx, """
            CREATE TABLE IF NOT EXISTS probe_task (singleton INTEGER PRIMARY KEY CHECK(singleton=1), task_hash TEXT NOT NULL, cap INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS probe_batch (batch_hash TEXT PRIMARY KEY, cap INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS probe_trial (batch_hash TEXT NOT NULL, trial_hash TEXT NOT NULL, cap INTEGER NOT NULL, PRIMARY KEY(batch_hash,trial_hash));
            CREATE TABLE IF NOT EXISTS probe_request (request_hash TEXT PRIMARY KEY, batch_hash TEXT NOT NULL, trial_hash TEXT NOT NULL,
                operation_hash TEXT NOT NULL, binding_hash TEXT NOT NULL, state TEXT NOT NULL, input_tokens INTEGER, output_tokens INTEGER);
            """);
        using var command = Command(db, tx, "SELECT task_hash,cap FROM probe_task WHERE singleton=1");
        using var reader = command.ExecuteReader();
        var exists = reader.Read();
        if (exists && (reader.GetString(0) != taskHash || reader.GetInt32(1) != totalLimit))
            throw Error("REVIEW_DISPATCH_LIMITS_CHANGED");
        reader.Close();
        if (!exists) Execute(db, tx, "INSERT INTO probe_task VALUES(1,$task,$cap)", ("$task", taskHash), ("$cap", totalLimit));
        tx.Commit();
    }

    public void Register(string batchHash, string trialHash, int batchLimit, int trialLimit)
    {
        Digest(batchHash); Digest(trialHash); Limit(batchLimit); Limit(trialLimit);
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        var batch = Scalar(db, tx, "SELECT cap FROM probe_batch WHERE batch_hash=$b", ("$b", batchHash));
        var trial = Scalar(db, tx, "SELECT cap FROM probe_trial WHERE batch_hash=$b AND trial_hash=$t", ("$b", batchHash), ("$t", trialHash));
        if ((batch is not null && batch != batchLimit) || (trial is not null && trial != trialLimit))
            throw Error("REVIEW_DISPATCH_LIMITS_CHANGED");
        if (batch is null) Execute(db, tx, "INSERT INTO probe_batch VALUES($b,$cap)", ("$b", batchHash), ("$cap", batchLimit));
        if (trial is null) Execute(db, tx, "INSERT INTO probe_trial VALUES($b,$t,$cap)", ("$b", batchHash), ("$t", trialHash), ("$cap", trialLimit));
        tx.Commit();
    }

    public void Reserve(ReviewDispatchBinding binding)
    {
        Validate(binding);
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        if (Scalar(db, tx, "SELECT COUNT(*) FROM probe_request WHERE request_hash=$r", ("$r", binding.RequestHash)) != 0)
            throw Error("REVIEW_DISPATCH_ALREADY_CHARGED");
        var batch = Scalar(db, tx, "SELECT cap FROM probe_batch WHERE batch_hash=$b", ("$b", binding.BatchHash));
        var trial = Scalar(db, tx, "SELECT cap FROM probe_trial WHERE batch_hash=$b AND trial_hash=$t", ("$b", binding.BatchHash), ("$t", binding.TrialHash));
        if (batch is null || trial is null) throw Error("REVIEW_DISPATCH_NOT_REGISTERED");
        if (Scalar(db, tx, "SELECT COUNT(*) FROM probe_request") >= Scalar(db, tx, "SELECT cap FROM probe_task WHERE singleton=1") ||
            Scalar(db, tx, "SELECT COUNT(*) FROM probe_request WHERE batch_hash=$b", ("$b", binding.BatchHash)) >= batch ||
            Scalar(db, tx, "SELECT COUNT(*) FROM probe_request WHERE batch_hash=$b AND trial_hash=$t", ("$b", binding.BatchHash), ("$t", binding.TrialHash)) >= trial)
            throw Error("REVIEW_DISPATCH_BUDGET_LIMITED");
        Execute(db, tx, "INSERT INTO probe_request VALUES($r,$b,$t,$operation,$binding,'Reserved',NULL,NULL)",
            ("$r", binding.RequestHash), ("$b", binding.BatchHash), ("$t", binding.TrialHash),
            ("$operation", binding.OperationHash), ("$binding", BindingHash(binding)));
        tx.Commit(); // All three counters are derived from this durable, never-refunded row.
    }

    public void Finish(ReviewDispatchBinding binding, ChatUsage? usage)
    {
        Validate(binding);
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        var known = usage is { InputTokens: > 0, OutputTokens: > 0 };
        Execute(db, tx, """
            UPDATE probe_request SET state=$state,input_tokens=$input,output_tokens=$output
            WHERE request_hash=$r AND binding_hash=$binding AND state='Reserved'
            """, ("$state", known ? "Completed" : "Unknown"), ("$input", known ? usage!.InputTokens : DBNull.Value),
            ("$output", known ? usage!.OutputTokens : DBNull.Value), ("$r", binding.RequestHash), ("$binding", BindingHash(binding)));
        tx.Commit();
    }

    // Explicit session recovery, only after old dispatch owners have stopped. Opening a
    // second connection does not recover or cancel another connection's live work.
    public void RecoverInterrupted()
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        Execute(db, tx, "UPDATE probe_request SET state='Unknown' WHERE state='Reserved'"); tx.Commit();
    }

    public IReadOnlyList<ReviewDispatchRecord> Read()
    {
        using var db = Open(); using var command = Command(db, null,
            "SELECT request_hash,operation_hash,state,input_tokens,output_tokens FROM probe_request ORDER BY request_hash");
        using var reader = command.ExecuteReader(); var rows = new List<ReviewDispatchRecord>();
        while (reader.Read()) rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetInt32(3), reader.IsDBNull(4) ? null : reader.GetInt32(4)));
        return rows.AsReadOnly();
    }

    internal static void Validate(ReviewDispatchBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        Digest(binding.BatchHash); Digest(binding.TrialHash); Digest(binding.RequestHash);
        Digest(binding.OperationHash); Digest(binding.InputHash); Validate(binding.Context);
    }
    internal static void Validate(ReviewDispatchContext context)
    {
        ArgumentNullException.ThrowIfNull(context); Digest(context.ScopeHash); Digest(context.RouteHash);
        if (context.PolicyGeneration < 0) throw Error("REVIEW_DISPATCH_INVALID_BINDING");
    }
    private static void Digest(string value)
    {
        if (value is null || value.Length != 64 || value.Any(c => !(c is >= '0' and <= '9' or >= 'A' and <= 'F')))
            throw Error("REVIEW_DISPATCH_INVALID_BINDING");
    }
    private static void Limit(int value) { if (value < 1) throw Error("REVIEW_DISPATCH_INVALID_LIMIT"); }
    private static string BindingHash(ReviewDispatchBinding binding)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(binding))));
    internal static AgentProtocolException Error(string code) => new(code);
    private SqliteConnection Open()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false, DefaultTimeout = 15 }.ToString());
        try { db.Open(); return db; } catch { db.Dispose(); throw; }
    }
    private static SqliteCommand Command(SqliteConnection db, SqliteTransaction? tx, string sql, params (string Key, object Value)[] args)
    {
        var command = db.CreateCommand(); command.Transaction = tx; command.CommandText = sql;
        foreach (var (key, value) in args) command.Parameters.AddWithValue(key, value);
        return command;
    }
    private static long? Scalar(SqliteConnection db, SqliteTransaction tx, string sql, params (string Key, object Value)[] args)
    {
        using var command = Command(db, tx, sql, args);
        return command.ExecuteScalar() is long value ? value : null;
    }
    private static void Execute(SqliteConnection db, SqliteTransaction tx, string sql, params (string Key, object Value)[] args)
    { using var command = Command(db, tx, sql, args); command.ExecuteNonQuery(); }
}

internal sealed class ReviewDispatchAuthorityProbe(Action<UnderstandingDiagnostic>? diagnostics = null)
{
    private readonly object _gate = new();
    private sealed class ActiveDispatch(CancellationTokenSource source, ReviewDispatchBinding binding)
    {
        internal CancellationTokenSource Source { get; } = source;
        internal ReviewDispatchBinding Binding { get; } = binding;
        internal Task? Cancellation { get; set; }
    }
    private readonly Dictionary<CancellationTokenSource, ActiveDispatch> _active = [];
    private ReviewDispatchContext? _context;
    private long _epoch;
    private bool _authorized; // Default OFF; a new authority never resumes old permits.

    public void Configure(ReviewDispatchContext context, bool authorized)
    {
        ReviewDispatchLedgerProbe.Validate(context);
        lock (_gate)
        {
            _context = context; _authorized = authorized; _epoch++;
            // CancelAsync marks the token synchronously but schedules user callbacks.
            // A callback may reenter Configure; only the dispatch lifetime drains it.
            foreach (var active in _active.Values)
                active.Cancellation ??= CancelCallbacksAsync(active);
        }
    }

    private async Task CancelCallbacksAsync(ActiveDispatch active)
    {
        try { await active.Source.CancelAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { /* A completed operation no longer needs cancellation. */ }
        catch (AggregateException) { Log(active.Binding, "dispatch_cancel_callback_failed", "REVIEW_DISPATCH_CANCEL_CALLBACK_FAILED"); }
    }

    public ReviewDispatchPermitProbe Issue(ReviewDispatchLedgerProbe ledger, ReviewDispatchBinding binding)
    {
        ArgumentNullException.ThrowIfNull(ledger); ReviewDispatchLedgerProbe.Validate(binding);
        lock (_gate)
        {
            if (!_authorized || _context != binding.Context) throw ReviewDispatchLedgerProbe.Error("REVIEW_DISPATCH_NOT_AUTHORIZED");
            return new(this, ledger, binding, _epoch);
        }
    }

    internal async Task<T> RunAsync<T>(ReviewDispatchPermitProbe permit, ReviewDispatchBinding binding,
        Action<CancellationToken> ensureCurrent, Func<CancellationToken, Task<T>> start,
        Func<T, ChatUsage?> usage, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(ensureCurrent); ArgumentNullException.ThrowIfNull(start); ArgumentNullException.ThrowIfNull(usage);
        token.ThrowIfCancellationRequested();
        ReviewDispatchLedgerProbe.Validate(binding);
        Log(binding, "dispatch_received");
        CancellationTokenSource? linked = null; var charged = false;
        try
        {
            Task<T> work;
            lock (_gate)
            {
                Check();
                if (permit.Used) throw ReviewDispatchLedgerProbe.Error("REVIEW_DISPATCH_PERMIT_USED");
                permit.Used = true;
                ensureCurrent(token); Check(); token.ThrowIfCancellationRequested();
                permit.Ledger.Reserve(binding); charged = true;
                linked = CancellationTokenSource.CreateLinkedTokenSource(token); _active.Add(linked, new(linked, binding));
                ensureCurrent(linked.Token); Check(); linked.Token.ThrowIfCancellationRequested();
                // Linearization is the synchronous handoff, not remote HTTP receipt.
                // No await or diagnostic callback separates the last check from handoff.
                work = start(linked.Token) ?? throw ReviewDispatchLedgerProbe.Error("REVIEW_DISPATCH_INVALID_HANDOFF");
            }
            Log(binding, "dispatch_charged");
            var result = await work.ConfigureAwait(false);
            permit.Ledger.Finish(binding, usage(result));
            Log(binding, "dispatch_completed");
            linked.Token.ThrowIfCancellationRequested();
            lock (_gate) { Check(); ensureCurrent(linked.Token); Check(); linked.Token.ThrowIfCancellationRequested(); }
            return result;
        }
        catch
        {
            if (charged)
            {
                try { permit.Ledger.Finish(binding, null); }
                catch (Exception) { Log(binding, "dispatch_accounting_failed", "REVIEW_DISPATCH_ACCOUNTING_FAILED"); }
            }
            Log(binding, "dispatch_failed", "REVIEW_DISPATCH_FAILED");
            throw; // Accounting/callback failure must not replace the primary error or cancellation.
        }
        finally
        {
            if (linked is not null)
            {
                ActiveDispatch? active;
                lock (_gate) _active.Remove(linked, out active);
                if (active?.Cancellation is { } callbacks) await callbacks.ConfigureAwait(false);
                linked.Dispose();
            }
        }
        void Check()
        {
            if (permit.Owner != this || permit.Binding != binding) throw ReviewDispatchLedgerProbe.Error("REVIEW_DISPATCH_BINDING_MISMATCH");
            if (!_authorized || permit.Epoch != _epoch || _context != binding.Context)
                throw ReviewDispatchLedgerProbe.Error("REVIEW_DISPATCH_NOT_AUTHORIZED");
        }
    }

    private void Log(ReviewDispatchBinding binding, string stage, string? code = null)
    {
        try { diagnostics?.Invoke(new(DateTimeOffset.UtcNow, binding.RequestHash[..16], stage, code)); }
        catch (Exception) { /* Metadata diagnostics never change dispatch or accounting. */ }
    }
}

internal sealed class ReviewDispatchPermitProbe(ReviewDispatchAuthorityProbe owner, ReviewDispatchLedgerProbe ledger,
    ReviewDispatchBinding binding, long epoch)
{
    internal ReviewDispatchAuthorityProbe Owner { get; } = owner;
    internal ReviewDispatchLedgerProbe Ledger { get; } = ledger;
    internal ReviewDispatchBinding Binding { get; } = binding;
    internal long Epoch { get; } = epoch;
    internal bool Used { get; set; }
    public Task<T> RunAsync<T>(ReviewDispatchBinding actual, Action<CancellationToken> ensureCurrent,
        Func<CancellationToken, Task<T>> start, Func<T, ChatUsage?> usage, CancellationToken token = default)
        => Owner.RunAsync(this, actual, ensureCurrent, start, usage, token);
}
