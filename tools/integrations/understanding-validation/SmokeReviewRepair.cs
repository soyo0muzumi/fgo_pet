using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Settings;
using FgoPet.Understanding;
using FgoPet.Understanding.Contracts;
using Microsoft.Data.Sqlite;

namespace FgoPet.Testing.Understanding;

/// <summary>One explicit SMOKE schema repair from saved originals; never a formal replay or a quota reset.</summary>
internal static class SmokeReviewRepair
{
    private const string TrialId="SMOKE-BOUNDED";
    internal static async Task<int> RunAsync(string repository,string root,ReviewCallLedger ledger,
        ModelConnectionSettings connection,bool live,string implementation)
    {
        var report=Path.Combine(repository,"docs","internal","reports","personal-p4-local-20261018","p4-m2-20261020");
        var fullRoot=Path.GetFullPath(root);
        if(live?fullRoot!=Path.GetFullPath(Path.Combine(report,"live")):
            !fullRoot.StartsWith(Path.GetFullPath(Path.Combine(report,"offline-smoke-review-repair"))+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("VALIDATION_SMOKE_REPAIR_ROOT_DENIED");
        var manifestPath=Path.Combine(root,"smoke","manifest.json");var run=ValidationRun.ReadManifest(manifestPath);
        var fixture=TrialFixtures.Smoke().Single(f=>f.Id==TrialId);var trial=run.Trials.Single(t=>t.Id==TrialId);
        if(run.Schema!=1||run.Live!=live||run.Batch!="Smoke"||run.SemanticAcceptance||run.Trials.Count!=3||
            trial.Status!="PROTOCOL_FAILED"||trial.Code!="VALIDATION_REVIEW_INCOMPLETE"||trial.SmokeReviewRepairs!=0||
            trial.Directory!=$"{TrialId}-attempt-{trial.Attempts}")
            throw new InvalidOperationException("VALIDATION_SMOKE_REPAIR_NOT_ELIGIBLE");
        var directory=Path.Combine(root,"smoke",trial.Directory);var original=Path.Combine(directory,"trial-result.json");
        using var previous=JsonDocument.Parse(File.ReadAllText(original));var exchanges=previous.RootElement.GetProperty("exchanges").EnumerateArray().ToArray();
        if(exchanges.Length!=3||!exchanges[0].GetProperty("complete").GetBoolean()||!exchanges[1].GetProperty("complete").GetBoolean()||
            exchanges[2].GetProperty("complete").GetBoolean())throw new InvalidOperationException("VALIDATION_SMOKE_REPAIR_NOT_ELIGIBLE");
        var failure=exchanges[2].GetProperty("failure");
        var signals=failure.GetProperty("signals").EnumerateArray().Select(v=>v.GetString()).ToArray();
        using var rejected=JsonDocument.Parse(exchanges[2].GetProperty("wire").GetString()!);
        if(failure.GetProperty("status").GetInt32()!=400||failure.GetProperty("category").GetString()!="InvalidResponse"||
            !signals.Contains("response_format")||!signals.Contains("JSON")||
            rejected.RootElement.GetProperty("response_format").GetProperty("type").GetString()!="json_object"||
            rejected.RootElement.GetProperty("messages").EnumerateArray().Any(m=>m.GetProperty("content").GetString()!.Contains("json",StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("VALIDATION_SMOKE_JSON_FAILURE_NOT_CONFIRMED");
        var before=ledger.Snapshot();var trialHash=ValidationHost.Digest(TrialId);
        var used=before.Calls.Count(c=>c.Batch==ReviewBatch.Smoke&&c.TrialHash==trialHash);
        if(used!=trial.ChargedAttempts||8-used<3||24-before.BatchUsed[ReviewBatch.Smoke]<3)
            throw new InvalidOperationException("VALIDATION_SMOKE_REPAIR_BUDGET_UNAVAILABLE");
        var manifestBackup=Path.Combine(root,"smoke","manifest-before-json-review-repair.json");
        if(File.Exists(manifestBackup))throw new InvalidOperationException("VALIDATION_SMOKE_REPAIR_ALREADY_STARTED");
        File.Copy(manifestPath,manifestBackup);Backup(Path.Combine(directory,"runtime.db"),Path.Combine(directory,"runtime-before-json-review-repair.db"));
        trial.PriorSmokeAttempts.Add(new{trial.Directory,trial.Status,trial.Code,trial.Implementation,trial.ChargedAttempts,
            preservedResult="trial-result.json",resultSha256=ValidationGate.FileHash(original),reason="ONE_JSON_MODE_SCHEMA_REPAIR_FROM_SAVED_ORIGINALS"});
        trial.SmokeReviewRepairs=1;trial.Status="REVIEW_REPAIR_STARTED";trial.Code=null;trial.Implementation=implementation;
        ValidationHost.Write(manifestPath,run);var log=new MetadataLog(Path.Combine(directory,"driver-events.jsonl"));
        log.Event(TrialId,"explicit_smoke_json_review_repair_started");ValidationHost? host=null;
        try
        {
            host=await ValidationHost.CreateAsync(repository,directory,fixture,ReviewBatch.Smoke,ledger,connection,live,trial.ClockTicks).ConfigureAwait(false);
            host.SaveEvidence("before-json-review-repair");
            using var current=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"before-json-review-repair.json")));
            foreach(var name in new[]{"conversations","state"})
                if(!JsonNode.DeepEquals(JsonNode.Parse(previous.RootElement.GetProperty(name).GetRawText()),JsonNode.Parse(current.RootElement.GetProperty(name).GetRawText())))
                    throw new InvalidOperationException("VALIDATION_SMOKE_REPAIR_ORIGINAL_STATE_CHANGED");
            await host.ReviewAsync().ConfigureAwait(false);host.SaveEvidence("after-json-review-repair");
            log.Event(TrialId,"review_repair_completed");host.NewSession();host.SaveEvidence("before-repaired-followup");
            var reply=await host.SendAsync(fixture.Followup).ConfigureAwait(false);
            if(reply.Status!=ConversationSendStatus.Completed)throw new InvalidOperationException("VALIDATION_REPLY_INCOMPLETE");
            trial.Status="COMPLETED";log.Event(TrialId,"trial_protocol_completed");
        }
        catch(Exception error){trial.Status="PROTOCOL_FAILED";trial.Code=Program.Safe(error);log.Event(TrialId,"exception_exit",trial.Code);}
        finally
        {
            if(host is not null)
            {trial.ClockTicks=host.Clock.Ticks;host.SaveEvidence("json-review-repair-result");await host.DisposeAsync().ConfigureAwait(false);}
            trial.ChargedAttempts+=ledger.Snapshot().TotalUsed-before.TotalUsed;
            ValidationHost.Write(manifestPath,run);ValidationHost.Write(Path.Combine(root,"smoke","ledger-snapshot.json"),ledger.Snapshot());
            ValidationRun.WriteReviewPacket(Path.Combine(root,"smoke"),run);
        }
        Console.WriteLine(JsonSerializer.Serialize(new{status="SMOKE_REVIEW_REPAIR_RECORDED",live,protocolStatus=trial.Status,safeCode=trial.Code,
            totalCharged=ledger.Snapshot().TotalUsed,trialCharged=trial.ChargedAttempts,originalFailurePreserved=true,semanticAccepted=false}));
        return trial.Status=="COMPLETED"?0:1;
    }
    internal static async Task<int> VerifyOfflineAsync(string repository,string report)
    {
        var sourceRoot=Path.Combine(report,"live");var canonicalBefore=CountReadonly(Path.Combine(sourceRoot,"dispatch-ledger.db"));
        var oldRun=ValidationRun.ReadManifest(Path.Combine(sourceRoot,"smoke","manifest.json"));
        var old=oldRun.Trials.Single(t=>t.Id==TrialId);
        var root=Path.Combine(report,"offline-smoke-review-repair",Guid.NewGuid().ToString("N"));
        var directory=Path.Combine(root,"smoke",old.Directory);Directory.CreateDirectory(directory);
        var source=Path.Combine(sourceRoot,"smoke",old.Directory);
        Backup(Path.Combine(source,"runtime.db"),Path.Combine(directory,"runtime.db"));
        File.Copy(Path.Combine(source,"trial-result.json"),Path.Combine(directory,"trial-result.json"));
        var run=new BatchRun{Batch="Smoke",Live=false,Implementation="OFFLINE-SAVED-SYNTHETIC-MATERIAL",Trials=TrialFixtures.Smoke().Select(f=>f.Id==TrialId?old:new TrialRun{Id=f.Id}).ToList()};
        ValidationHost.Write(Path.Combine(root,"smoke","manifest.json"),run);
        var connection=new ModelConnectionSettings("deepseek","https://api.deepseek.com/v1","deepseek-flash");
        var scope=new ConversationScope(ValidationHost.Role,"p4-"+TrialId.ToLowerInvariant());
        var routeHash=ReviewBudgetedChatProvider.RouteHash(ModelRouteKey.From(connection));
        var ledger=new ReviewCallLedger(Path.Combine(root,"dispatch-ledger.db"));
        ledger.Approve(new("offline-smoke-review-repair",routeHash,new('A',64),true));ledger.RegisterTrial(ReviewBatch.Smoke,TrialId,scope.Key,8);
        using var oldLedger=JsonDocument.Parse(File.ReadAllText(Path.Combine(sourceRoot,"smoke","ledger-snapshot.json")));
        var calls=oldLedger.RootElement.GetProperty("Calls").EnumerateArray().Where(c=>c.GetProperty("TrialHash").GetString()==ValidationHost.Digest(TrialId)).ToArray();
        if(calls.Length!=5||old.ChargedAttempts!=5)throw new InvalidOperationException("VALIDATION_OFFLINE_REPAIR_FIXTURE_CHANGED");
        foreach(var call in calls)
        {
            var reservation=ledger.TryReserve(new(ReviewBatch.Smoke,TrialId,scope.Key,"synthetic-history-"+ledger.Snapshot().TotalUsed,
                routeHash,call.GetProperty("Purpose").GetString()!,UnderstandingMode.Bounded,call.GetProperty("PolicyGeneration").GetInt64(),
                call.GetProperty("InputHash").GetString()!,call.GetProperty("EstimatedInputTokens").GetInt32()))
                ??throw new InvalidOperationException("VALIDATION_OFFLINE_REPAIR_SEED_FAILED");
            ledger.Complete(reservation.RequestHash,ReviewCallOutcome.Unknown);
        }
        var code=await RunAsync(repository,root,ledger,connection,false,"OFFLINE-SAVED-SYNTHETIC-REPAIR").ConfigureAwait(false);
        if(code!=0||ledger.Snapshot().TotalUsed!=8||CountReadonly(Path.Combine(sourceRoot,"dispatch-ledger.db"))!=canonicalBefore)
            throw new InvalidOperationException("VALIDATION_OFFLINE_SMOKE_REPAIR_FAILED");
        ValidationHost.Write(Path.Combine(root,"verification.json"),new{passed=true,realProviderCalls=0,
            historicalCallsArtificiallySeeded=5,newSyntheticCalls=3,trialHardLimit=8,canonicalLedgerUnchanged=true,
            originalFixture="Saved synthetic SMOKE originals and known pre-repair JSON-mode failure; all new transport and credentials are substitutes",
            semanticAcceptance=false});
        Console.WriteLine(JsonSerializer.Serialize(new{status="OFFLINE_SMOKE_REVIEW_REPAIR_VERIFIED",passed=true,realProviderCalls=0,syntheticCalls=3,trialHardLimit=8}));return 0;
    }
    private static int CountReadonly(string path)
    {
        using var database=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString());
        database.Open();using var command=database.CreateCommand();command.CommandText="SELECT COUNT(*) FROM review_calls";return Convert.ToInt32(command.ExecuteScalar());
    }
    private static void Backup(string source,string target)
    {
        if(File.Exists(target))throw new InvalidOperationException("VALIDATION_SMOKE_REPAIR_BACKUP_EXISTS");
        using var input=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=source,Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString());
        using var output=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=target,Mode=SqliteOpenMode.ReadWriteCreate,Pooling=false}.ToString());
        input.Open();output.Open();input.BackupDatabase(output);
    }
}
