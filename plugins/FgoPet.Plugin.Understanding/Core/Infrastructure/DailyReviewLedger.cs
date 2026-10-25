using System.Security.Cryptography;
using System.Text;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Understanding.Contracts;
using Microsoft.Data.Sqlite;

namespace FgoPet.Understanding;

public sealed record DailyReviewStatus(bool Authorized, int Limit, int Used, DateTimeOffset ResetsAt, string? Code);

/// <summary>Daily background dispatches, independent of conversation backups and immutable trial approvals.</summary>
public sealed class DailyReviewLedger(string databasePath, TimeProvider clock) : IReviewCallBudget
{
    private readonly RuntimeDatabase _database = new(databasePath, pooling: false);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool Digest(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigit);
    public static void ValidateLimit(int limit)
    { if (limit is < 1 or > 10000) throw new ArgumentException("UNDERSTANDING_DAILY_LIMIT_INVALID"); }

    public void Authorize(string scope, string routeHash, int dailyLimit)
    {
        ValidateLimit(dailyLimit);
        if (string.IsNullOrWhiteSpace(scope) || !Digest(routeHash)) throw new ArgumentException("UNDERSTANDING_GRANT_INVALID");
        using var db = Open(); using var tx = db.BeginTransaction();
        Execute(db, tx, "UPDATE daily_config SET call_limit=$limit WHERE id=1", ("$limit", dailyLimit));
        Execute(db, tx, "INSERT INTO daily_grants VALUES($scope,$route) ON CONFLICT(scope_hash) DO UPDATE SET route_hash=$route",
            ("$scope", Hash(scope)), ("$route", routeHash));
        tx.Commit();
    }

    public void Revoke(string scope)
    {
        using var db = Open(); using var tx = db.BeginTransaction();
        Execute(db, tx, "DELETE FROM daily_grants WHERE scope_hash=$scope", ("$scope", Hash(scope))); tx.Commit();
    }

    public DailyReviewStatus Status(string scope, string routeHash)
    {
        using var db = Open(); using var tx = db.BeginTransaction();
        var status = ReadStatus(db, tx, scope, routeHash); tx.Commit(); return status;
    }

    public ReviewCallReservation? TryReserve(ReviewCallPlan plan)
    {
        if (plan.Mode is not (UnderstandingMode.Proposals or UnderstandingMode.Bounded) || plan.PolicyGeneration < 0 ||
            plan.Purpose is not ("review" or "classify") || plan.EstimatedInputTokens is < 1 or > 16000 ||
            string.IsNullOrWhiteSpace(plan.ScopeKey) || string.IsNullOrWhiteSpace(plan.RequestId) || plan.RequestId.Length > 128 ||
            !Digest(plan.RouteHash) || !Digest(plan.InputHash)) return null;
        using var db = Open(); using var tx = db.BeginTransaction();
        var status = ReadStatus(db, tx, plan.ScopeKey, plan.RouteHash);
        if (status.Code is not null) { tx.Commit(); return null; }
        var hash = Hash(plan.RequestId);
        using var existing = Command(db, tx, "SELECT COUNT(*) FROM daily_calls WHERE request_hash=$id", ("$id", hash));
        if ((long)existing.ExecuteScalar()! != 0) { tx.Commit(); return null; }
        Execute(db, tx, """
            INSERT INTO daily_calls VALUES($id,$day,$scope,$route,$purpose,$generation,$input,$estimate,0,NULL,NULL,$at)
            """, ("$id", hash), ("$day", Day()), ("$scope", Hash(plan.ScopeKey)), ("$route", plan.RouteHash),
            ("$purpose", plan.Purpose), ("$generation", plan.PolicyGeneration), ("$input", plan.InputHash),
            ("$estimate", plan.EstimatedInputTokens), ("$at", clock.GetUtcNow().ToString("O")));
        tx.Commit(); return new(hash, 1, status.Used + 1, status.Used + 1, status.Used + 1);
    }

    public void Complete(string requestHash, ReviewCallOutcome outcome, int? inputTokens = null, int? outputTokens = null)
    {
        if (!Digest(requestHash) || !Enum.IsDefined(outcome) || outcome == ReviewCallOutcome.ReservedOrUnknown ||
            inputTokens < 0 || outputTokens < 0 || (inputTokens is null) != (outputTokens is null))
            throw new ArgumentException("UNDERSTANDING_DAILY_RESULT_INVALID");
        using var db = Open(); using var tx = db.BeginTransaction();
        Execute(db, tx, "UPDATE daily_calls SET outcome=$outcome,input_tokens=$input,output_tokens=$output WHERE request_hash=$id AND outcome=0",
            ("$outcome", (int)outcome), ("$input", inputTokens), ("$output", outputTokens), ("$id", requestHash)); tx.Commit();
    }

    private long Day() => clock.GetUtcNow().ToUnixTimeSeconds() / 86400;
    private DailyReviewStatus ReadStatus(SqliteConnection db, SqliteTransaction tx, string scope, string route)
    {
        var today = Day();
        using var config = Command(db, tx, "SELECT call_limit,last_day FROM daily_config WHERE id=1");
        int limit; long last;
        using (var r = config.ExecuteReader())
        { if (!r.Read()) throw new InvalidOperationException("UNDERSTANDING_DAILY_STORE_INVALID"); limit = r.GetInt32(0); last = r.GetInt64(1); }
        ValidateLimit(limit);
        if (today > last) Execute(db, tx, "UPDATE daily_config SET last_day=$day WHERE id=1", ("$day", today));
        var day = Math.Max(today, last);
        using var count = Command(db, tx, "SELECT COUNT(*) FROM daily_calls WHERE day=$day", ("$day", day));
        var used = checked((int)(long)count.ExecuteScalar()!);
        using var grant = Command(db, tx, "SELECT route_hash FROM daily_grants WHERE scope_hash=$scope", ("$scope", Hash(scope)));
        var allowed = grant.ExecuteScalar() is string bound && bound == route;
        return new(allowed, limit, used, DateTimeOffset.FromUnixTimeSeconds((day + 1) * 86400),
            today < last ? "UNDERSTANDING_CLOCK_ROLLBACK" : !allowed ? "UNDERSTANDING_BUDGET_UNAUTHORIZED" :
            used >= limit ? "UNDERSTANDING_BUDGET_LIMITED" : null);
    }

    private SqliteConnection Open()
    {
        var db = _database.Open();
        try
        {
            using var tx = db.BeginTransaction();
            using var exists = Command(db, tx, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='daily_schema'");
            if ((long)exists.ExecuteScalar()! == 0)
            {
                using var tables = Command(db, tx, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'");
                if ((long)tables.ExecuteScalar()! != 0) throw new InvalidOperationException("UNDERSTANDING_DAILY_WRONG_STORE");
                Execute(db, tx, """
                    CREATE TABLE daily_schema(version INTEGER NOT NULL);
                    INSERT INTO daily_schema VALUES(1);
                    CREATE TABLE daily_config(id INTEGER PRIMARY KEY CHECK(id=1),call_limit INTEGER NOT NULL,last_day INTEGER NOT NULL);
                    INSERT INTO daily_config VALUES(1,12,$day);
                    CREATE TABLE daily_grants(scope_hash TEXT PRIMARY KEY,route_hash TEXT NOT NULL);
                    CREATE TABLE daily_calls(request_hash TEXT PRIMARY KEY,day INTEGER NOT NULL,scope_hash TEXT NOT NULL,
                        route_hash TEXT NOT NULL,purpose TEXT NOT NULL,generation INTEGER NOT NULL,input_hash TEXT NOT NULL,
                        estimate INTEGER NOT NULL,outcome INTEGER NOT NULL,input_tokens INTEGER NULL,output_tokens INTEGER NULL,at_utc TEXT NOT NULL);
                    CREATE INDEX daily_calls_day ON daily_calls(day);
                    """, ("$day", Day()));
            }
            using var version = Command(db, tx, "SELECT version FROM daily_schema");
            if (version.ExecuteScalar() is not long v || v != 1) throw new InvalidOperationException("UNDERSTANDING_DAILY_SCHEMA_UNSUPPORTED");
            tx.Commit(); return db;
        }
        catch { db.Dispose(); throw; }
    }
    private static SqliteCommand Command(SqliteConnection db, SqliteTransaction tx, string sql, params (string Name, object? Value)[] args)
    {
        var command = db.CreateCommand(); command.Transaction = tx; command.CommandText = sql;
        foreach (var (name, value) in args) command.Parameters.AddWithValue(name, value ?? DBNull.Value); return command;
    }
    private static void Execute(SqliteConnection db, SqliteTransaction tx, string sql, params (string Name, object? Value)[] args)
    { using var command = Command(db, tx, sql, args); command.ExecuteNonQuery(); }
}
