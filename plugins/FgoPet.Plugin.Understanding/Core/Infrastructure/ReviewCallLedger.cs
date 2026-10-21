using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Understanding.Contracts;
using Microsoft.Data.Sqlite;

namespace FgoPet.Understanding;

/// <summary>Durable pre-dispatch accounting in a separate metadata-only store; no refund operation.</summary>
public sealed class ReviewCallLedger(string databasePath) : IReviewCallBudget
{
    private readonly RuntimeDatabase _database = new(databasePath, pooling: false);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool Id(string? value) => value is { Length: >0 and <=128 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
    private static bool Digest(string? value) => value is { Length:64 } && value.All(char.IsAsciiHexDigit);
    private static bool Scope(string? value) => value is { Length: >0 and <=512 } && !value.Contains('\0');
    private static int BatchLimit(ReviewBudgetApproval grant, ReviewBatch batch) => batch switch
    { ReviewBatch.Smoke => grant.SmokeLimit, ReviewBatch.Formal => grant.FormalLimit, ReviewBatch.Repair => grant.RepairLimit,
      _ => throw new ArgumentException("REVIEW_LEDGER_INVALID_BATCH") };

    public void Approve(ReviewBudgetApproval approval)
    {
        ArgumentNullException.ThrowIfNull(approval);
        if (!approval.HumanReviewConfirmed || !Id(approval.Id) || !Digest(approval.RouteHash) || !Digest(approval.ApprovalHash) ||
            approval.SmokeLimit is <0 or >24 || approval.FormalLimit is <0 or >114 || approval.RepairLimit is <0 or >114 ||
            approval.TotalLimit is <0 or >252 || approval.TotalLimit > approval.SmokeLimit+approval.FormalLimit+approval.RepairLimit)
            throw new ArgumentException("REVIEW_LEDGER_APPROVAL_INVALID");
        using var db=Open();using var tx=db.BeginTransaction();
        var canonical=JsonSerializer.Serialize(approval with { Id=Hash(approval.Id), RouteHash=approval.RouteHash.ToUpperInvariant(),
            ApprovalHash=approval.ApprovalHash.ToUpperInvariant() });
        using var existing=Command(db,tx,"SELECT approval_json FROM review_budget WHERE singleton=1");
        if (existing.ExecuteScalar() is string previous)
        {
            if (previous!=canonical) throw new InvalidOperationException("REVIEW_LEDGER_APPROVAL_IMMUTABLE");
            return;
        }
        Execute(db,tx,"INSERT INTO review_budget VALUES(1,$json)",("$json",canonical));tx.Commit();
    }

    public void RegisterTrial(ReviewBatch batch,string trialId,string scopeKey,int limit)
    {
        if (!Enum.IsDefined(batch) || !Id(trialId) || !Scope(scopeKey) || limit<1 ||
            limit>(batch==ReviewBatch.Smoke ? 24 : trialId.StartsWith("R3B-",StringComparison.Ordinal) ? 2 : 6) ||
            batch!=ReviewBatch.Smoke && !Regex.IsMatch(trialId,"^R(?:1|2|3A|3B|4|5A|5B)-[123]$",RegexOptions.CultureInvariant))
            throw new ArgumentException("REVIEW_LEDGER_TRIAL_INVALID");
        using var db=Open();using var tx=db.BeginTransaction();
        if (Grant(db,tx) is null) throw new InvalidOperationException("REVIEW_LEDGER_UNAPPROVED");
        using var old=Command(db,tx,"SELECT scope_hash,call_limit FROM review_trials WHERE batch=$batch AND trial_hash=$trial",
            ("$batch",(int)batch),("$trial",Hash(trialId)));
        using (var reader=old.ExecuteReader())
            if(reader.Read())
            {
                if(reader.GetString(0)!=Hash(scopeKey) || reader.GetInt32(1)!=limit)
                    throw new InvalidOperationException("REVIEW_LEDGER_TRIAL_IMMUTABLE");
                return;
            }
        Execute(db,tx,"INSERT INTO review_trials VALUES($batch,$trial,$scope,$limit)",
            ("$batch",(int)batch),("$trial",Hash(trialId)),("$scope",Hash(scopeKey)),("$limit",limit));tx.Commit();
    }

    public ReviewCallReservation? TryReserve(ReviewCallPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if(!Enum.IsDefined(plan.Batch) || !Id(plan.TrialId) || !Scope(plan.ScopeKey) || !Id(plan.RequestId) ||
            !Digest(plan.RouteHash) || !Digest(plan.InputHash) || !Enum.IsDefined(plan.Mode) || plan.PolicyGeneration<0 ||
            plan.EstimatedInputTokens is <1 or >16000 || plan.Purpose is not ("reply" or "review" or "classify" or "summary")) return null;
        using var db=Open();using var tx=db.BeginTransaction();
        var grant=Grant(db,tx);
        if(grant is null || !string.Equals(grant.RouteHash,plan.RouteHash,StringComparison.OrdinalIgnoreCase))return null;
        var request=Hash(plan.RequestId);var trial=Hash(plan.TrialId);var scope=Hash(plan.ScopeKey);
        using(var duplicate=Command(db,tx,"SELECT COUNT(*) FROM review_calls WHERE request_hash=$request",("$request",request)))
            if((long)duplicate.ExecuteScalar()!>0)return null;
        int trialLimit;
        using(var trialCommand=Command(db,tx,"SELECT call_limit FROM review_trials WHERE batch=$batch AND trial_hash=$trial AND scope_hash=$scope",
            ("$batch",(int)plan.Batch),("$trial",trial),("$scope",scope)))
        {
            if(trialCommand.ExecuteScalar() is not long allowed)return null;
            trialLimit=checked((int)allowed);
        }
        using var counts=Command(db,tx,"""
            SELECT COUNT(*),COALESCE(SUM(batch=$batch),0),COALESCE(SUM(batch=$batch AND trial_hash=$trial),0),
              COALESCE(SUM(batch=$batch AND trial_hash=$trial AND purpose=$purpose),0) FROM review_calls
            """,("$batch",(int)plan.Batch),("$trial",trial),("$purpose",plan.Purpose));
        int total,batchUsed,trialUsed,attempt;
        using(var reader=counts.ExecuteReader())
        { reader.Read();total=reader.GetInt32(0);batchUsed=reader.GetInt32(1);trialUsed=reader.GetInt32(2);attempt=reader.GetInt32(3)+1; }
        if(total>=grant.TotalLimit || batchUsed>=BatchLimit(grant,plan.Batch) || trialUsed>=trialLimit)return null;
        Execute(db,tx,"""
            INSERT INTO review_calls VALUES($request,$batch,$trial,$scope,$purpose,$attempt,$mode,$generation,$input,$estimate,0,NULL,NULL,$at)
            """,("$request",request),("$batch",(int)plan.Batch),("$trial",trial),("$scope",scope),("$purpose",plan.Purpose),
            ("$attempt",attempt),("$mode",(int)plan.Mode),("$generation",plan.PolicyGeneration),("$input",plan.InputHash.ToUpperInvariant()),
            ("$estimate",plan.EstimatedInputTokens),("$at",DateTimeOffset.UtcNow.ToString("O")));
        tx.Commit();return new(request,attempt,total+1,batchUsed+1,trialUsed+1);
    }

    public void Complete(string requestHash,ReviewCallOutcome outcome,int? inputTokens=null,int? outputTokens=null)
    {
        if(!Digest(requestHash) || !Enum.IsDefined(outcome) || outcome==ReviewCallOutcome.ReservedOrUnknown || inputTokens<0 || outputTokens<0 ||
            (inputTokens is null)!=(outputTokens is null))throw new ArgumentException("REVIEW_LEDGER_RESULT_INVALID");
        using var db=Open();using var tx=db.BeginTransaction();
        Execute(db,tx,"UPDATE review_calls SET outcome=$outcome,input_tokens=$input,output_tokens=$output WHERE request_hash=$request AND outcome=0",
            ("$request",requestHash.ToUpperInvariant()),("$outcome",(int)outcome),("$input",inputTokens),("$output",outputTokens));tx.Commit();
    }

    public ReviewBudgetSnapshot Snapshot()
    {
        using var db=Open();using var command=Command(db,null,"""
            SELECT request_hash,batch,trial_hash,scope_hash,purpose,attempt,mode,policy_generation,input_hash,estimate,outcome,input_tokens,output_tokens
            FROM review_calls ORDER BY rowid
            """);using var reader=command.ExecuteReader();var calls=new List<ReviewCallRecord>();
        while(reader.Read())calls.Add(new(reader.GetString(0),(ReviewBatch)reader.GetInt32(1),reader.GetString(2),reader.GetString(3),
            reader.GetString(4),reader.GetInt32(5),(UnderstandingMode)reader.GetInt32(6),reader.GetInt64(7),reader.GetString(8),reader.GetInt32(9),
            (ReviewCallOutcome)reader.GetInt32(10),reader.IsDBNull(11)?null:reader.GetInt32(11),reader.IsDBNull(12)?null:reader.GetInt32(12)));
        return new(calls.Count,Enum.GetValues<ReviewBatch>().ToDictionary(b=>b,b=>calls.Count(c=>c.Batch==b)),calls);
    }

    private SqliteConnection Open()
    {
        var db=_database.Open();
        try
        {
            using var tx=db.BeginTransaction();
            using var schema=Command(db,tx,"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='review_ledger_schema'");
            if((long)schema.ExecuteScalar()!>0)
            {
                using var version=Command(db,tx,"SELECT version FROM review_ledger_schema WHERE singleton=1");
                if(version.ExecuteScalar() is not long current || current!=1)throw new InvalidOperationException("REVIEW_LEDGER_SCHEMA_UNSUPPORTED");
            }
            else
            {
                using var nonempty=Command(db,tx,"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'");
                if((long)nonempty.ExecuteScalar()!>0)throw new InvalidOperationException("REVIEW_LEDGER_WRONG_STORE");
                Execute(db,tx,"""
                    CREATE TABLE review_ledger_schema(singleton INTEGER PRIMARY KEY CHECK(singleton=1),version INTEGER NOT NULL);
                    INSERT INTO review_ledger_schema VALUES(1,1);
                    CREATE TABLE review_budget(singleton INTEGER PRIMARY KEY CHECK(singleton=1),approval_json TEXT NOT NULL);
                    CREATE TABLE review_trials(batch INTEGER NOT NULL,trial_hash TEXT NOT NULL,scope_hash TEXT NOT NULL,call_limit INTEGER NOT NULL,
                      PRIMARY KEY(batch,trial_hash));
                    CREATE TABLE review_calls(request_hash TEXT PRIMARY KEY,batch INTEGER NOT NULL,trial_hash TEXT NOT NULL,scope_hash TEXT NOT NULL,
                      purpose TEXT NOT NULL,attempt INTEGER NOT NULL,mode INTEGER NOT NULL,policy_generation INTEGER NOT NULL,input_hash TEXT NOT NULL,
                      estimate INTEGER NOT NULL,outcome INTEGER NOT NULL,input_tokens INTEGER NULL,output_tokens INTEGER NULL,at_utc TEXT NOT NULL,
                      FOREIGN KEY(batch,trial_hash) REFERENCES review_trials(batch,trial_hash));
                    """);
            }
            tx.Commit();return db;
        }
        catch { db.Dispose();throw; }
    }
    private static ReviewBudgetApproval? Grant(SqliteConnection db,SqliteTransaction tx)
    { using var command=Command(db,tx,"SELECT approval_json FROM review_budget WHERE singleton=1");return command.ExecuteScalar() is string json?JsonSerializer.Deserialize<ReviewBudgetApproval>(json):null; }
    private static SqliteCommand Command(SqliteConnection db,SqliteTransaction? tx,string sql,params (string Name,object? Value)[] args)
    { var command=db.CreateCommand();command.Transaction=tx;command.CommandText=sql;foreach(var(name,value)in args)command.Parameters.AddWithValue(name,value??DBNull.Value);return command; }
    private static void Execute(SqliteConnection db,SqliteTransaction tx,string sql,params (string Name,object? Value)[] args)
    { using var command=Command(db,tx,sql,args);command.ExecuteNonQuery(); }
}
