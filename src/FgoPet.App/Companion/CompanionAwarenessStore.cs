using System.IO;
using System.Security.Cryptography;
using System.Text;
using FgoPet.Extensibility;
using FgoPet.Kernel.Companion;
using Microsoft.Data.Sqlite;

namespace FgoPet.App.Companion;

internal sealed record CompanionAwarenessPolicy(CompanionAwarenessOptions Options, string? ApprovedRoute);

/// <summary>Separate local consent and cost ledger. Contains no source content, window grants or credentials.</summary>
internal sealed class CompanionAwarenessStore
{
    private readonly string _path;
    private readonly string _marker;
    private readonly string _revoked;
    private readonly object _gate = new();
    private bool _opened;
    private bool _failed;

    public CompanionAwarenessStore(string path)
    {
        _path = Path.GetFullPath(path);
        _marker = _path + ".initialized";
        _revoked = _path + ".permissions-revoked";
    }

    public bool Available { get { lock (_gate) { try { using var db = Open(); return true; } catch { _failed = true; return false; } } } }

    public CompanionAwarenessPolicy Read(string scopeKey)
    {
        lock (_gate)
        {
            try
            {
                using var db = Open();
                using var command = db.CreateCommand();
                command.CommandText = "SELECT activity, applications, background, daily_limit, approved_route FROM policies WHERE scope_key=$scope";
                command.Parameters.AddWithValue("$scope", scopeKey);
                using var row = command.ExecuteReader();
                if (!row.Read()) return new(new(), null);
                var limit = row.GetInt32(3);
                if (limit is < 1 or > 24) throw new InvalidDataException();
                return new(new(row.GetBoolean(0), row.GetBoolean(1), row.GetBoolean(2), limit),
                    row.IsDBNull(4) || File.Exists(_revoked) ? null : row.GetString(4));
            }
            catch { _failed = true; throw Unavailable(); }
        }
    }

    public void Save(string scopeKey, CompanionAwarenessPolicy policy)
    {
        if (policy.Options.DailyRequestLimit is < 1 or > 24) throw new ArgumentOutOfRangeException(nameof(policy));
        lock (_gate)
        {
            try
            {
                using var db = Open();
                using var transaction = db.BeginTransaction();
                using var command = db.CreateCommand();
                command.Transaction = transaction;
                if (File.Exists(_revoked))
                {
                    // A restore fence covers every former scope, not just the one being approved now.
                    command.CommandText = "UPDATE policies SET approved_route=NULL";
                    command.ExecuteNonQuery();
                }
                command.CommandText = """
                    INSERT INTO policies(scope_key, activity, applications, background, daily_limit, approved_route)
                    VALUES($scope,$activity,$apps,$background,$limit,$route)
                    ON CONFLICT(scope_key) DO UPDATE SET activity=$activity, applications=$apps,
                        background=$background, daily_limit=$limit, approved_route=$route
                    """;
                command.Parameters.AddWithValue("$scope", scopeKey);
                command.Parameters.AddWithValue("$activity", policy.Options.ActivityContextEnabled);
                command.Parameters.AddWithValue("$apps", policy.Options.ApplicationsEnabled);
                command.Parameters.AddWithValue("$background", policy.Options.BackgroundEnabled);
                command.Parameters.AddWithValue("$limit", policy.Options.DailyRequestLimit);
                command.Parameters.AddWithValue("$route", (object?)policy.ApprovedRoute ?? DBNull.Value);
                command.ExecuteNonQuery();
                transaction.Commit();
                if (File.Exists(_revoked)) File.Delete(_revoked);
            }
            catch { _failed = true; throw Unavailable(); }
        }
    }

    public int Used(DateTimeOffset now)
    {
        lock (_gate)
        {
            try
            {
                using var db = Open();
                using var transaction = db.BeginTransaction();
                var at = AdvanceClock(db, transaction, now);
                var used = Count(db, transaction, at);
                transaction.Commit();
                return used;
            }
            catch { _failed = true; return -1; }
        }
    }

    public bool TryReserve(CompanionActivityReservation reservation, int dailyLimit)
    {
        if (dailyLimit is < 1 or > 24 || reservation.InputTokens is < 1 or > 4096
            || reservation.MaxOutputTokens is < 1 or > 384 || string.IsNullOrWhiteSpace(reservation.ActivityId)) return false;
        lock (_gate)
        {
            try
            {
                using var db = Open();
                using var transaction = db.BeginTransaction();
                var at = AdvanceClock(db, transaction, reservation.ReservedAtUtc);
                if (Count(db, transaction, at) >= dailyLimit) { transaction.Commit(); return false; }
                using var command = db.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "INSERT OR IGNORE INTO charges(activity_key, at_ms) VALUES($id,$at)";
                command.Parameters.AddWithValue("$id", Hash(reservation.ActivityId));
                command.Parameters.AddWithValue("$at", at);
                var reserved = command.ExecuteNonQuery() == 1;
                transaction.Commit();
                return reserved;
            }
            catch { _failed = true; return false; }
        }
    }

    public void ForgetPolicies(string? scopeKey = null)
    {
        lock (_gate)
        {
            try
            {
                using var db = Open();
                using var command = db.CreateCommand();
                command.CommandText = scopeKey is null ? "DELETE FROM policies" : "DELETE FROM policies WHERE scope_key=$scope";
                if (scopeKey is not null) command.Parameters.AddWithValue("$scope", scopeKey);
                command.ExecuteNonQuery();
                // Operational charges intentionally survive source deletion, role changes and private restores.
            }
            catch { _failed = true; throw Unavailable(); }
        }
    }

    public bool RevokeForRestore()
    {
        lock (_gate)
        {
            var fenced = false;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                if (!File.Exists(_revoked))
                {
                    using var fence = new FileStream(_revoked, FileMode.CreateNew, FileAccess.Write,
                        FileShare.Read, 1, FileOptions.WriteThrough);
                    fence.WriteByte(1);
                    fence.Flush(flushToDisk: true);
                }
                fenced = true;
            }
            catch { /* The independent database deletion may still revoke the grants. */ }
            try { ForgetPolicies(); return true; }
            catch { _failed = true; return fenced; }
        }
    }

    private SqliteConnection Open()
    {
        if (_failed || (_opened || File.Exists(_marker)) && !File.Exists(_path)) throw Unavailable();
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var exists = File.Exists(_path);
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _path, Mode = exists ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadWriteCreate,
            Pooling = false, DefaultTimeout = 3
        }.ToString());
        try
        {
            db.Open();
            using var command = db.CreateCommand();
            if (!exists)
            {
                command.CommandText = """
                    PRAGMA synchronous=FULL;
                    CREATE TABLE ledger_state(id INTEGER PRIMARY KEY CHECK(id=1), version INTEGER NOT NULL, high_water_ms INTEGER NOT NULL);
                    INSERT INTO ledger_state VALUES(1,1,0);
                    CREATE TABLE policies(scope_key TEXT PRIMARY KEY, activity INTEGER NOT NULL, applications INTEGER NOT NULL,
                        background INTEGER NOT NULL, daily_limit INTEGER NOT NULL CHECK(daily_limit BETWEEN 1 AND 24), approved_route TEXT);
                    CREATE TABLE charges(activity_key TEXT PRIMARY KEY, at_ms INTEGER NOT NULL);
                    CREATE INDEX charges_by_time ON charges(at_ms);
                    """;
                command.ExecuteNonQuery();
            }
            // Opening a connection alone does not read SQLite pages. Check the authority row on every
            // access so a damaged/replaced file cannot leave cached source grants apparently current.
            command.CommandText = "SELECT version FROM ledger_state WHERE id=1";
            if (Convert.ToInt64(command.ExecuteScalar()) != 1) throw Unavailable();
            if (!_opened)
            {
                command.CommandText = "PRAGMA quick_check";
                if (!string.Equals(command.ExecuteScalar() as string, "ok", StringComparison.Ordinal)) throw Unavailable();
                if (!File.Exists(_marker)) File.WriteAllText(_marker, "1");
                _opened = true;
            }
            return db;
        }
        catch { db.Dispose(); throw; }
    }

    private static long AdvanceClock(SqliteConnection db, SqliteTransaction transaction, DateTimeOffset now)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE ledger_state SET high_water_ms=MAX(high_water_ms,$now) WHERE id=1 RETURNING high_water_ms";
        command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        return Convert.ToInt64(command.ExecuteScalar() ?? throw Unavailable());
    }

    private static int Count(SqliteConnection db, SqliteTransaction transaction, long at)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM charges WHERE at_ms > $after";
        command.Parameters.AddWithValue("$after", at - (long)TimeSpan.FromHours(24).TotalMilliseconds);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    internal static string ScopeKey(ToolScope scope) => Hash($"{scope.RoleId.Length}:{scope.RoleId}:{scope.ProjectId}");
    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static InvalidOperationException Unavailable() => new("AWARENESS_STORAGE_UNAVAILABLE");
}
