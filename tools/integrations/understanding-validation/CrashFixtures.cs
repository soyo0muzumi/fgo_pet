using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using FgoPet.Core.Settings;
using FgoPet.Understanding;
using FgoPet.Understanding.Contracts;

namespace FgoPet.Testing.Understanding;

internal sealed class CrashPoint(string directory,string stage)
{
    internal void Reached(string current)
    {
        if(current!=stage)return;
        ValidationHost.Write(Path.Combine(directory,"checkpoint.json"),new{stage,syntheticOnly=true,process=Environment.ProcessId});
        Thread.Sleep(Timeout.Infinite); // Test child only; the parent kills this exact owned process.
    }
}

internal static class CrashFixtures
{
    private static readonly ModelConnectionSettings Connection=new("deepseek","https://api.deepseek.com/v1","deepseek-flash");
    private static readonly string[] Cases=["off-dispatched","off-returned","off-displayed","proposals-before-save","proposals-after-save","bounded-after-save"];
    internal static async Task<int> RunAsync(string repository,string report)
    {
        var root=Path.Combine(report,"offline-faults",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var results=new List<object>();
        foreach(var name in Cases)
        {
            var directory=Path.Combine(root,name);Directory.CreateDirectory(directory);
            ValidationHost.Write(Path.Combine(directory,"synthetic-process-fixture.json"),new{name,syntheticOnly=true});
            var ledger=new ReviewCallLedger(Path.Combine(directory,"ledger.db"));
            ledger.Approve(new("offline-process-fixture",ReviewBudgetedChatProvider.RouteHash(FgoPet.Core.Dialogue.ModelRouteKey.From(Connection)),new('A',64),true));
            var info=new ProcessStartInfo("dotnet"){WorkingDirectory=repository,UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true};
            foreach(var argument in new[]{typeof(Program).Assembly.Location,"offline-crash-child",directory,name})info.ArgumentList.Add(argument);
            info.Environment["FGO_PET_STATE_ROOT"]=directory;
            using var process=Process.Start(info)??throw new InvalidOperationException("VALIDATION_CHILD_START_FAILED");
            var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();
            var signalled=false;
            try
            {
                var timer=Stopwatch.StartNew();
                while(timer.Elapsed<TimeSpan.FromSeconds(45)&&!process.HasExited)
                {
                    if(File.Exists(Path.Combine(directory,"checkpoint.json"))){signalled=true;break;}
                    await Task.Delay(50).ConfigureAwait(false);
                }
                if(!signalled)throw new InvalidOperationException("VALIDATION_CRASH_CHECKPOINT_NOT_REACHED");
                using var checkpoint=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"checkpoint.json")));
                if(checkpoint.RootElement.GetProperty("process").GetInt32()!=process.Id)throw new InvalidOperationException("VALIDATION_CHILD_ID_MISMATCH");
            }
            finally
            {
                if(!process.HasExited)process.Kill(entireProcessTree:true);
                await process.WaitForExitAsync().ConfigureAwait(false);
                await File.WriteAllTextAsync(Path.Combine(directory,"child-stdout.log"),await output.ConfigureAwait(false));
                await File.WriteAllTextAsync(Path.Combine(directory,"child-stderr.log"),await error.ConfigureAwait(false));
            }
            var before=ledger.Snapshot();
            if(before.TotalUsed!=(name=="bounded-after-save"?2:1))throw new InvalidOperationException("VALIDATION_CRASH_ACCOUNTING_FAILED");
            var fixture=Fixture(name);
            await using var host=await ValidationHost.CreateAsync(repository,directory,fixture,ReviewBatch.Smoke,ledger,Connection,false,TimeSpan.FromSeconds(120).Ticks).ConfigureAwait(false);
            if(name.StartsWith("off-",StringComparison.Ordinal))
            {
                host.Clock.Advance(TimeSpan.FromMinutes(20));await host.Scheduler.PulseAsync().ConfigureAwait(false);
                if(ledger.Snapshot().TotalUsed!=before.TotalUsed||host.Owner.List(host.Scope,true).Count!=0||host.Owner.List(host.Scope,false).Count!=0)
                    throw new InvalidOperationException("VALIDATION_OFF_RESTART_REPLAYED");
                var attempts=host.Owner.AttemptStates(host.Scope);
                if(attempts.Count!=1||(name=="off-displayed")!=(attempts[0].Status=="ReviewedNotPersisted"))
                    throw new InvalidOperationException("VALIDATION_OFF_DISPLAY_STATE_FAILED");
                foreach(var database in Directory.GetFiles(directory,"runtime.db*"))
                    if(File.ReadAllBytes(database).AsSpan().IndexOf(Encoding.UTF8.GetBytes("Synthetic bounded interpretation."))>=0)
                        throw new InvalidOperationException("VALIDATION_OFF_BODY_PERSISTED");
                if(name=="off-returned")
                {
                    host.Configure(UnderstandingMode.Proposals);var boundary=host.Owner.State(host.Scope).AutoSince;
                    host.AddOriginal("Synthetic new post-enable range.");await host.ReviewAsync().ConfigureAwait(false);
                    if(host.Owner.List(host.Scope,true).Any(e=>e.Sources.Any(s=>s.SourceRevision<=boundary)))
                        throw new InvalidOperationException("VALIDATION_OFF_OLD_RANGE_REPLAYED");
                }
            }
            else
            {
                await host.ReviewAsync().ConfigureAwait(false);
                var expected=before.TotalUsed+(name=="proposals-before-save"?1:0);
                if(ledger.Snapshot().TotalUsed!=expected||host.Owner.WorkSummary(host.Scope).PendingSources!=0)
                    throw new InvalidOperationException("VALIDATION_RECEIPT_RECOVERY_FAILED");
                if(name=="bounded-after-save"?host.Owner.Query(host.Scope,"").Items.Count!=1:host.Owner.List(host.Scope,true).Count!=1)
                    throw new InvalidOperationException("VALIDATION_CRASH_COMMIT_FAILED");
            }
            host.SaveEvidence("recovery-result");
            results.Add(new{name,passed=true,signalled,chargedBefore=before.TotalUsed,chargedAfter=ledger.Snapshot().TotalUsed,
                forcedTermination=true,syntheticHttpOnly=true,realProviderCalls=0});
            ValidationHost.Write(Path.Combine(root,"results.json"),new{results,semanticAcceptance=false});
        }
        Console.WriteLine(JsonSerializer.Serialize(new{status="OFFLINE_PROCESS_FAULTS_RECORDED",passed=results.Count,realProviderCalls=0}));return 0;
    }
    internal static async Task<int> ChildAsync(string repository,string report,string directory,string name)
    {
        var prefix=Path.GetFullPath(Path.Combine(report,"offline-faults"))+Path.DirectorySeparatorChar;
        directory=Path.GetFullPath(directory);
        if(!directory.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)||!Cases.Contains(name)||
            !File.Exists(Path.Combine(directory,"synthetic-process-fixture.json")))throw new InvalidOperationException("VALIDATION_CRASH_CHILD_DENIED");
        var stage=name switch{"off-dispatched"=>"transport_handoff","off-returned"=>"returned","off-displayed"=>"displayed",
            "proposals-before-save"=>"review_model_completed",_=>"batch_committed"};
        var crash=new CrashPoint(directory,stage);var ledger=new ReviewCallLedger(Path.Combine(directory,"ledger.db"));var fixture=Fixture(name);
        await using var host=await ValidationHost.CreateAsync(repository,directory,fixture,ReviewBatch.Smoke,ledger,Connection,false,crash:crash).ConfigureAwait(false);
        host.Prepare(fixture);
        if(name.StartsWith("off-",StringComparison.Ordinal))
        {
            await host.OffPreviewAsync("process-preview").ConfigureAwait(false);
            crash.Reached("returned");host.Reviews.ConfirmManualPreviewDisplayed(host.Scope,"process-preview");crash.Reached("displayed");
        }
        else{host.AddOriginal("Synthetic current material for process-crash recovery.");await host.ReviewAsync().ConfigureAwait(false);}
        throw new InvalidOperationException("VALIDATION_CRASH_CHECKPOINT_MISSED");
    }
    private static TrialFixture Fixture(string name)=>TrialFixtures.Smoke().Single(f=>f.Mode==(name.StartsWith("off-",StringComparison.Ordinal)?UnderstandingMode.Off:
        name.StartsWith("proposals-",StringComparison.Ordinal)?UnderstandingMode.Proposals:UnderstandingMode.Bounded));
}
