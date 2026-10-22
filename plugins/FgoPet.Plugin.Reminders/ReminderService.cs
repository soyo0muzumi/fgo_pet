using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FgoPet.Core.Dialogue;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace FgoPet.Plugin.Reminders;

/// <summary>Owns durable one-shot reminders, receipts and dispatch claims. No model calls.</summary>
public sealed class ReminderService(RuntimeDatabase database, TimeProvider clock) : IDerivedConversationData
{
    public static readonly TimeSpan CatchUpWindow = TimeSpan.FromDays(1);
    private readonly object _gate = new();
    private bool _ready;

    public ReminderDraft Validate(ReminderDraft draft)
    {
        if (string.IsNullOrWhiteSpace(draft.Message) || draft.Message.Length > 255 || !ValidUtf16(draft.Message) ||
            draft.Message.Any(c => char.IsControl(c) && c is not ('\n' or '\t')) ||
            string.IsNullOrWhiteSpace(draft.TimeZoneId) || draft.TimeZoneId.Length > 128)
            throw new ReminderException("REMINDER_INVALID_ARGUMENTS");
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(draft.TimeZoneId); }
        catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException)
        { throw new ReminderException("REMINDER_TIMEZONE_INVALID"); }
        // An explicit offset disambiguates a fall-back hour. A mismatching offset or
        // nonexistent spring-forward local time cannot silently choose another instant.
        if (zone.IsInvalidTime(draft.DueAt.DateTime) || zone.GetUtcOffset(draft.DueAt) != draft.DueAt.Offset)
            throw new ReminderException("REMINDER_TIMEZONE_MISMATCH");
        var delta = draft.DueAt - clock.GetUtcNow();
        if (delta <= TimeSpan.Zero || delta > TimeSpan.FromDays(366))
            throw new ReminderException("REMINDER_TIME_OUT_OF_RANGE");
        return draft;
    }

    public IReadOnlyList<Reminder> List(ToolScope scope, int offset = 0, int limit = 100)
    {
        ValidateScope(scope);
        if (offset < 0 || limit is < 1 or > 100) throw new ReminderException("REMINDER_INVALID_ARGUMENTS");
        lock (_gate)
        {
            EnsureSchema(); using var db = database.Open();
            return Read(db, null, """
                SELECT body FROM reminders WHERE scope=$scope
                ORDER BY CASE WHEN status IN ('Pending','Dispatching') THEN 0 ELSE 1 END,due_utc,id LIMIT $limit OFFSET $offset
                """, ("$scope", ScopeKey(scope)), ("$limit", limit + 1), ("$offset", offset));
        }
    }

    public Reminder? Get(ToolScope scope, string id)
    {
        ValidateScope(scope);
        lock (_gate)
        {
            EnsureSchema(); using var db = database.Open();
            return Get(db, null, scope, id);
        }
    }

    public Reminder Create(ToolScope scope, string key, string fingerprint, ReminderDraft draft)
    {
        ValidateScope(scope); ValidateIdentity(key, fingerprint);
        lock (_gate)
        {
            EnsureSchema(); using var db = database.Open(); using var tx = db.BeginTransaction();
            if (Receipt(db, tx, scope, key, fingerprint) is { } prior) return prior;
            Validate(draft);
            using (var count = Command(db, tx, "SELECT COUNT(*) FROM reminders WHERE scope=$scope AND status IN ('Pending','Dispatching')", ("$scope", ScopeKey(scope))))
                if ((long)count.ExecuteScalar()! >= 100) throw new ReminderException("REMINDER_CAPACITY");
            var now = clock.GetUtcNow();
            var item = new Reminder("reminder-" + Guid.NewGuid().ToString("N"), scope, draft.Message,
                draft.DueAt.ToUniversalTime(), draft.TimeZoneId, draft.DueAt, ReminderStatus.Pending, 1, now, now);
            Save(db, tx, item); SaveReceipt(db, tx, scope, key, fingerprint, item); tx.Commit(); return item;
        }
    }

    public Reminder Cancel(ToolScope scope, string key, string fingerprint, string id, long revision)
    {
        ValidateScope(scope); ValidateIdentity(key, fingerprint);
        lock (_gate)
        {
            EnsureSchema(); using var db = database.Open(); using var tx = db.BeginTransaction();
            if (Receipt(db, tx, scope, key, fingerprint) is { } prior) return prior;
            var item = Get(db, tx, scope, id) ?? throw new ReminderException("REMINDER_NOT_FOUND");
            if (item.Revision != revision) throw new ReminderException("REMINDER_VERSION_CONFLICT");
            // A notification already claimed by the dispatcher cannot be recalled.
            // Never report successful cancellation after a dispatch has begun.
            if (item.Status != ReminderStatus.Pending) throw new ReminderException("REMINDER_NOT_PENDING");
            item = item with { Status = ReminderStatus.Cancelled, Revision = item.Revision + 1, UpdatedAtUtc = clock.GetUtcNow() };
            Save(db, tx, item); SaveReceipt(db, tx, scope, key, fingerprint, item); tx.Commit(); return item;
        }
    }

    public Reminder? ClaimNext()
    {
        lock (_gate)
        {
            EnsureSchema(); using var db = database.Open(); using var tx = db.BeginTransaction();
            var now = clock.GetUtcNow();
            var due = Read(db, tx, "SELECT body FROM reminders WHERE status='Pending' AND due_utc<=$now ORDER BY due_utc,id LIMIT 100", ("$now", now.ToUnixTimeMilliseconds()));
            Reminder? claimed = null;
            foreach (var item in due)
            {
                var stale = now - item.DueAtUtc > CatchUpWindow;
                var next = item with { Status = stale ? ReminderStatus.Expired : ReminderStatus.Dispatching,
                    Revision = item.Revision + 1, UpdatedAtUtc = now, ErrorCode = stale ? "REMINDER_CATCHUP_EXPIRED" : null };
                Save(db, tx, next);
                if (!stale) { claimed = next; break; }
            }
            tx.Commit(); return claimed;
        }
    }

    public void Finish(Reminder claimed, ReminderStatus status, string? code = null)
    {
        if (status is not (ReminderStatus.Submitted or ReminderStatus.Failed or ReminderStatus.Unknown or ReminderStatus.Pending))
            throw new ArgumentOutOfRangeException(nameof(status));
        lock (_gate)
        {
            EnsureSchema(); using var db = database.Open(); using var tx = db.BeginTransaction();
            var current = Get(db, tx, claimed.Scope, claimed.Id);
            if (current is null || current.Status != ReminderStatus.Dispatching || current.Revision != claimed.Revision) return;
            Save(db, tx, current with { Status = status, Revision = current.Revision + 1, UpdatedAtUtc = clock.GetUtcNow(), ErrorCode = code });
            tx.Commit();
        }
    }

    public void RecoverInterrupted()
    {
        lock (_gate)
        {
            EnsureSchema(); using var db = database.Open(); using var tx = db.BeginTransaction();
            foreach (var item in Read(db, tx, "SELECT body FROM reminders WHERE status='Dispatching'"))
                Save(db, tx, item with { Status = ReminderStatus.Unknown, Revision = item.Revision + 1,
                    UpdatedAtUtc = clock.GetUtcNow(), ErrorCode = "REMINDER_DELIVERY_UNKNOWN" });
            tx.Commit();
        }
    }

    public void ForgetConversation(string conversationId, string roleId) => Forget(conversationId, roleId);
    public void ForgetAll() => Forget(null, null);
    private void Forget(string? conversationId, string? roleId)
    {
        lock (_gate)
        {
            EnsureSchema(); using var db = database.Open(); using var tx = db.BeginTransaction();
            var where = conversationId is null ? "" : " WHERE conversation_id=$conversation AND role_id=$role";
            Execute(db, tx, "DELETE FROM reminders" + where, ("$conversation", conversationId), ("$role", roleId));
            Execute(db, tx, "DELETE FROM reminder_receipts" + where, ("$conversation", conversationId), ("$role", roleId));
            tx.Commit();
        }
    }
    public JsonElement Export()
    {
        lock (_gate)
        {
            EnsureSchema(); using var db = database.Open();
            return JsonSerializer.SerializeToElement(new { reminders = Read(db, null, "SELECT body FROM reminders ORDER BY due_utc,id") });
        }
    }
    public void NormalizeRestoredCopy(string validatedStagingDatabasePath)
    {
        if (string.Equals(Path.GetFullPath(validatedStagingDatabasePath), database.DatabasePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("REMINDER_RESTORE_REQUIRES_STAGING");
        var staging = new ReminderService(new RuntimeDatabase(validatedStagingDatabasePath, pooling: false), clock);
        // Restoring a backup never re-arms historical notifications or deleted content.
        // Reminders are conversation-linked actions, not a new ongoing authorization.
        staging.ForgetAll();
    }

    private void EnsureSchema()
    {
        if (_ready) return;
        new RuntimeDatabaseMigrator(database).MigrateModule("reminders", [new(1, """
            CREATE TABLE reminders(id TEXT PRIMARY KEY,scope TEXT NOT NULL,conversation_id TEXT NOT NULL,role_id TEXT NOT NULL,
              due_utc INTEGER NOT NULL,status TEXT NOT NULL,body TEXT NOT NULL);
            CREATE INDEX reminders_due ON reminders(status,due_utc);
            CREATE TABLE reminder_receipts(scope TEXT NOT NULL,operation_id TEXT NOT NULL,fingerprint TEXT NOT NULL,
              conversation_id TEXT NOT NULL,role_id TEXT NOT NULL,body TEXT NOT NULL,PRIMARY KEY(scope,operation_id));
            """)]);
        _ready = true;
    }
    private static Reminder? Get(SqliteConnection db, SqliteTransaction? tx, ToolScope scope, string id) =>
        Read(db, tx, "SELECT body FROM reminders WHERE scope=$scope AND id=$id", ("$scope", ScopeKey(scope)), ("$id", id)).SingleOrDefault();
    private static Reminder? Receipt(SqliteConnection db, SqliteTransaction tx, ToolScope scope, string key, string fingerprint)
    {
        using var cmd = Command(db, tx, "SELECT fingerprint,body FROM reminder_receipts WHERE scope=$scope AND operation_id=$key", ("$scope", ScopeKey(scope)), ("$key", key));
        using var r = cmd.ExecuteReader(); if (!r.Read()) return null;
        if (r.GetString(0) != fingerprint) throw new ReminderException("REMINDER_IDEMPOTENCY_CONFLICT");
        return JsonSerializer.Deserialize<Reminder>(r.GetString(1))!;
    }
    private static void SaveReceipt(SqliteConnection db, SqliteTransaction tx, ToolScope scope, string key, string fingerprint, Reminder item) =>
        Execute(db, tx, "INSERT INTO reminder_receipts VALUES($scope,$key,$fingerprint,$conversation,$role,$body)",
            ("$scope", ScopeKey(scope)), ("$key", key), ("$fingerprint", fingerprint), ("$conversation", scope.ConversationId), ("$role", scope.RoleId), ("$body", JsonSerializer.Serialize(item)));
    private static void Save(SqliteConnection db, SqliteTransaction tx, Reminder item) => Execute(db, tx,
        "INSERT INTO reminders VALUES($id,$scope,$conversation,$role,$due,$status,$body) ON CONFLICT(id) DO UPDATE SET status=$status,body=$body",
        ("$id", item.Id), ("$scope", ScopeKey(item.Scope)), ("$conversation", item.Scope.ConversationId), ("$role", item.Scope.RoleId),
        ("$due", item.DueAtUtc.ToUnixTimeMilliseconds()), ("$status", item.Status.ToString()), ("$body", JsonSerializer.Serialize(item)));
    private static List<Reminder> Read(SqliteConnection db, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = Command(db, tx, sql, args); using var r = cmd.ExecuteReader(); var items = new List<Reminder>();
        while (r.Read()) items.Add(JsonSerializer.Deserialize<Reminder>(r.GetString(0))!); return items;
    }
    private static SqliteCommand Command(SqliteConnection db, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] args)
    {
        var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value); return cmd;
    }
    private static void Execute(SqliteConnection db, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] args)
    { using var cmd = Command(db, tx, sql, args); cmd.ExecuteNonQuery(); }
    private static string ScopeKey(ToolScope scope) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(scope)));
    private static void ValidateScope(ToolScope scope)
    {
        if (scope is null || string.IsNullOrWhiteSpace(scope.ConversationId) || string.IsNullOrWhiteSpace(scope.RoleId))
            throw new ReminderException("REMINDER_SCOPE_DENIED");
    }
    private static void ValidateIdentity(string key, string fingerprint)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128 || fingerprint.Length != 64 || !fingerprint.All(Uri.IsHexDigit))
            throw new ReminderException("REMINDER_CONTEXT_REQUIRED");
    }
    private static bool ValidUtf16(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (!char.IsSurrogate(value[i])) continue;
            if (!char.IsHighSurrogate(value[i]) || i + 1 == value.Length || !char.IsLowSurrogate(value[++i])) return false;
        }
        return true;
    }
}
