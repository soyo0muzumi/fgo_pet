using System.IO;
using System.Diagnostics;
using System.Text.Json;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Settings;
using FgoPet.Infrastructure.Providers;
using FgoPet.Testing.Tokenization;
using FgoPet.Understanding;
using FgoPet.Understanding.Contracts;

namespace FgoPet.Testing.Understanding;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            if(args.Length==3&&args[0]=="subscription-review-ui")
                return SubscriptionScenarios.ReviewUi(Report(Repository()),args[1],args[2]);
            if(args.Length is 2 or 3&&args[0]=="review-ui")
                return HumanReviewWindow.Run(Report(Repository()),args[1],args.Length==3&&args[2]=="repair");
            if(args.Length==3&&args[0]=="offline-crash-child")
                return CrashFixtures.ChildAsync(Repository(),Report(Repository()),args[1],args[2]).GetAwaiter().GetResult();
            return RunAsync(args).GetAwaiter().GetResult();
        }
        catch(Exception error){Console.WriteLine(JsonSerializer.Serialize(new{status="VALIDATION_STOPPED",code=Safe(error),errorType=error.GetType().Name,
            callsites=new StackTrace(error,false).GetFrames().Take(5).Select(f=>f.GetMethod()?.Name).ToArray(),realCallsNotRefunded=true}));return 1;}
    }
    private static async Task<int> RunAsync(string[] args)
    {
        if(args.Length==1&&args[0]=="subscription-proposal-offline")
            return await ProposalIsolationRegression.RunAsync(Repository(),Report(Repository())).ConfigureAwait(false);
        if(args.FirstOrDefault()is "subscription-failures"or"subscription-offline"or"subscription-followup"or"subscription-retry"or"subscription-formal"or"subscription-formal-offline")
            return await SubscriptionScenarios.RunAsync(Repository(),Report(Repository()),args).ConfigureAwait(false);
        var reference=args.FirstOrDefault()=="offline-reference-formal";
        if(args.Length==0||args[0]is not("preregister"or"route-metadata"or"offline-smoke"or"offline-formal"or"offline-reference-formal"or"offline-faults"or"offline-smoke-review-repair"or"smoke-review-repair"or"smoke"or"freeze"or"formal"or"freeze-repair"or"repair")||
            args.Length!=(reference?4:args[0]=="freeze-repair"?2:1))
        {Console.WriteLine("Commands: preregister | offline-smoke | offline-formal | offline-reference-formal PYTHON WORKER ASSETS | smoke | freeze | formal | review-ui TRIAL [repair] | freeze-repair REASON_CODE | repair. Default has no network access.");return 2;}
        var repository=Repository();var report=Report(repository);
        if(args[0]=="offline-faults")return await CrashFixtures.RunAsync(repository,report).ConfigureAwait(false);
        if(args[0]=="offline-smoke-review-repair")return await SmokeReviewRepair.VerifyOfflineAsync(repository,report).ConfigureAwait(false);
        if(!File.Exists(Path.Combine(report,"authority.json")))throw new InvalidOperationException("VALIDATION_AUTHORITY_MISSING");
        var plan=new{version=1,attributedReviewer="user",syntheticOnly=true,fixtures=TrialFixtures.Formal(),smoke=TrialFixtures.Smoke(),
            scoring=TrialFixtures.Scoring(),acceptanceSha256=ValidationHost.Digest(File.ReadAllText(Path.Combine(repository,"fgo_pet_companion_acceptance_v4.md"))),
            outputLimit=2048,inputLimit=16000,limits=new{smoke=24,formal=114,repair=114,total=252},
            clock="2026-10-20T00:00:00Z; controlled monotonic review windows; original background one-minute timeout retained",
            route="official DeepSeek deepseek-flash; unchanged current connection parameters; server revision cannot be pinned",
            human="All semantic items require user yes/no/unclear and one actual exact-version UI approval; no program self-signature",
            boundaries="No production settings, private history, native tool writes, commits, installation or external completion reporting."};
        var canonical=JsonSerializer.Serialize(plan,new JsonSerializerOptions{WriteIndented=true});var planPath=Path.Combine(report,"preregistered-trials.json");
        if(args[0]=="preregister")
        {
            if(File.Exists(planPath)&&File.ReadAllText(planPath)!=canonical)throw new InvalidOperationException("VALIDATION_PREREGISTRATION_IMMUTABLE");
            if(!File.Exists(planPath))File.WriteAllText(planPath,canonical);
            Console.WriteLine(JsonSerializer.Serialize(new{status="PREREGISTERED",formalTrials=21,smokeTrials=3,sha256=ValidationHost.Digest(canonical),realProviderCalls=0}));return 0;
        }
        if(!File.Exists(planPath)||File.ReadAllText(planPath)!=canonical)throw new InvalidOperationException("VALIDATION_PREREGISTRATION_REQUIRED");
        if(args[0]=="route-metadata")
        {
            var current=ReadConnection();var metadata=new{route=current,routeSha256=ValidationGate.RouteHash(current),
                ledgerRouteSha256=ReviewBudgetedChatProvider.RouteHash(ModelRouteKey.From(current)),realProviderCalls=0};
            var path=Path.Combine(report,"route-metadata.json");
            if(File.Exists(path))throw new InvalidOperationException("VALIDATION_ROUTE_METADATA_ALREADY_EXISTS");
            ValidationHost.Write(path,metadata);Console.WriteLine(JsonSerializer.Serialize(metadata));return 0;
        }
        var live=args[0]is "smoke"or"smoke-review-repair"or"freeze"or"formal"or"freeze-repair"or"repair";
        var connection=live?ReadConnection():new ModelConnectionSettings("deepseek","https://api.deepseek.com/v1","deepseek-flash");
        if(args[0]is "freeze"or"freeze-repair")
        {
            var hash=ValidationGate.Freeze(report,repository,connection,args[0]=="freeze-repair",args.ElementAtOrDefault(1));
            Console.WriteLine(JsonSerializer.Serialize(new{status="BATCH_FROZEN",sha256=hash,newProviderCalls=0}));return 0;
        }
        var batch=args[0]is "formal"or"offline-formal"or"offline-reference-formal"?ReviewBatch.Formal:args[0]=="repair"?ReviewBatch.Repair:ReviewBatch.Smoke;
        var implementation=live?(batch==ReviewBatch.Smoke?ValidationGate.RequireEngineering(report,repository,connection):
            ValidationGate.RequireFrozen(report,repository,connection,batch==ReviewBatch.Repair)):(reference?"OFFLINE-REFERENCE-":"OFFLINE-")+ValidationGate.FileHash(typeof(Program).Assembly.Location);
        var route=ModelRouteKey.From(connection);
        var root=live?Path.Combine(report,"live"):Path.Combine(report,"offline-driver",batch.ToString().ToLowerInvariant()+"-"+Guid.NewGuid().ToString("N"));
        if(live&&batch!=ReviewBatch.Smoke&&!File.Exists(Path.Combine(root,"dispatch-ledger.db")))throw new InvalidOperationException("VALIDATION_EXISTING_LEDGER_REQUIRED");
        Directory.CreateDirectory(root);
        var ledger=new ReviewCallLedger(Path.Combine(root,"dispatch-ledger.db"));
        var authority=File.ReadAllText(Path.Combine(report,"authority.json"));
        ledger.Approve(new(live?"p4-20261020-approved":"offline-synthetic",ReviewBudgetedChatProvider.RouteHash(route),ValidationHost.Digest(authority),true));
        if(args[0]=="smoke-review-repair")return await SmokeReviewRepair.RunAsync(repository,root,ledger,connection,true,implementation).ConfigureAwait(false);
        using var counter=reference?new PipeCounter(args[1],Path.GetFullPath(args[2]),Path.GetFullPath(args[3])):null;
        if(reference&&counter?.Ready!=true)throw new InvalidOperationException("VALIDATION_REFERENCE_ASSETS_UNVERIFIED");
        Func<ModelRouteKey,IRequestTokenMeter>? meterFactory=counter is null?null:r=>new LocalReferenceRequestTokenMeter(r,counter.PolicyHash,counter);
        if(counter is not null)
        {
            ValidationHost.Write(Path.Combine(root,"local-reference-startup.json"),counter.Startup);
            var forbiddenDirectory=Path.Combine(root,"live-override-must-not-start");
            var ledgerBefore=ValidationGate.FileHash(Path.Combine(root,"dispatch-ledger.db"));
            try
            {
                await using var forbidden=await ValidationHost.CreateAsync(repository,forbiddenDirectory,TrialFixtures.Formal()[0],batch,ledger,connection,true,
                    meterFactory:_=>throw new InvalidOperationException("VALIDATION_REFERENCE_GUARD_NOT_ENFORCED")).ConfigureAwait(false);
                throw new InvalidOperationException("VALIDATION_REFERENCE_GUARD_NOT_ENFORCED");
            }
            catch(InvalidOperationException error)when(error.Message=="VALIDATION_LIVE_REFERENCE_METER_DENIED"){}
            if(Directory.Exists(forbiddenDirectory)||ValidationGate.FileHash(Path.Combine(root,"dispatch-ledger.db"))!=ledgerBefore)
                throw new InvalidOperationException("VALIDATION_REFERENCE_GUARD_SIDE_EFFECT");
            ValidationHost.Write(Path.Combine(root,"local-reference-live-guard.json"),new{liveOverrideRejected=true,hostStateCreated=false,ledgerUnchanged=true,realProviderCalls=0});
        }
        try{return await ValidationRun.RunAsync(repository,root,batch,ledger,connection,live,implementation,meterFactory).ConfigureAwait(false);}
        finally{if(counter is not null)ValidationHost.Write(Path.Combine(root,"local-reference-requests.json"),counter.Metadata);}
    }
    private static string Repository()
    {
        var cursor=new DirectoryInfo(AppContext.BaseDirectory);
        while(cursor is not null&&!File.Exists(Path.Combine(cursor.FullName,"FgoPet.sln")))cursor=cursor.Parent;
        if(cursor is null||!cursor.FullName.EndsWith(Path.Combine(".worktree","personal"),StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("VALIDATION_WRONG_WORKTREE");
        return cursor.FullName;
    }
    private static string Report(string repository)=>Path.Combine(repository,"docs","internal","reports","personal-p4-local-20261018","p4-m2-20261020");
    internal static ModelConnectionSettings ReadConnection()
    {
        var path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"FgoPet","settings.json");
        using var document=JsonDocument.Parse(File.ReadAllText(path));var c=document.RootElement.GetProperty("model_connection");
        var connection=new ModelConnectionSettings(c.GetProperty("provider_id").GetString()!,c.GetProperty("base_url").GetString()!,c.GetProperty("model_id").GetString()!,
            c.TryGetProperty("tools_supported",out var tools)?tools.GetBoolean():true,
            c.TryGetProperty("context_window_override",out var context)&&context.ValueKind!=JsonValueKind.Null?context.GetInt32():null,
            c.TryGetProperty("max_output_tokens",out var output)&&output.ValueKind!=JsonValueKind.Null?output.GetInt32():2048);
        if(connection.ProviderId!="deepseek"||connection.ModelId!="deepseek-flash"||connection.BaseUrl.TrimEnd('/')!="https://api.deepseek.com/v1"||connection.MaxOutputTokens!=2048)
            throw new InvalidOperationException("VALIDATION_ROUTE_NOT_APPROVED");
        return connection;
    }
    internal static string Safe(Exception error)=>error is InvalidOperationException&&error.Message.Length<=96&&error.Message.All(c=>c is >= 'A'and <= 'Z'or >= '0'and <= '9'or '_')
        ?error.Message:error is OperationCanceledException?"VALIDATION_CANCELLED":error is TimeoutException?"VALIDATION_TIMEOUT":"VALIDATION_FAILED";
}
