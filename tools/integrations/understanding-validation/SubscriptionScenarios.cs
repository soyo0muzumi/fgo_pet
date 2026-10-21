using System.IO;
using System.Text.Json;
using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Settings;
using FgoPet.Infrastructure.Providers;
using FgoPet.Understanding;
using FgoPet.Understanding.Contracts;

namespace FgoPet.Testing.Understanding;

internal static class SubscriptionScenarios
{
    private const string FormalEngineeringGateFile="formal-engineering-gate-v5.json";
    private static readonly string[] FailedIds=["R2-2","R3A-1","R3A-2","R4-1","R4-2","R4-3","R5A-1","R5A-2","R5A-3","R5B-1","R5B-2","R5B-3"];
    internal static string Root(string report,string id)
    {
        if(!id.StartsWith("run-",StringComparison.Ordinal)||id.Length!=36||!id[4..].All(char.IsAsciiHexDigit))
            throw new InvalidOperationException("SUBSCRIPTION_RUN_ID_INVALID");
        return Path.Combine(SubscriptionOptions.Stage(report),"runs",id);
    }
    internal static int ReviewUi(string report,string id,string trial)=>HumanReviewWindow.Run(Root(report,id),trial,false);
    internal static async Task<int> RunAsync(string repository,string report,string[] args)
    {
        var live=args[0]is not("subscription-offline"or"subscription-formal-offline");
        var full=args[0]is "subscription-formal"or"subscription-formal-offline";
        var resume=args[0]=="subscription-followup";
        var retry=args[0]=="subscription-retry";
        if(args.Length!=(resume?3:retry?2:1))throw new InvalidOperationException("SUBSCRIPTION_ARGUMENTS_INVALID");
        var stage=SubscriptionOptions.Stage(report);var connection=SubscriptionOptions.ReadConnection(stage);
        var limits=SubscriptionOptions.Capacity(connection);
        var original=ValidationRun.ReadManifest(Path.Combine(report,"live","repair","manifest.json"));
        if(!original.Trials.Where(t=>t.Status=="PROTOCOL_FAILED").Select(t=>t.Id).SequenceEqual(FailedIds))
            throw new InvalidOperationException("SUBSCRIPTION_ORIGINAL_FAILURE_SET_CHANGED");
        if(!live)CheckAccounting(stage,connection,limits);
        var allIds=TrialFixtures.Formal().Select(f=>f.Id).ToArray();
        var selected=full?allIds:FailedIds;
        string? previousManifest=null;
        if(retry)
        {
            previousManifest=Path.Combine(Root(report,args[1]),"live","formal","manifest.json");
            var previous=ValidationRun.ReadManifest(previousManifest);
            if(!previous.Live||previous.Trials.Any(t=>!FailedIds.Contains(t.Id))||
                previous.Trials.Select(t=>t.Id).Distinct().Count()!=previous.Trials.Count)
                throw new InvalidOperationException("SUBSCRIPTION_RETRY_SOURCE_INVALID");
            selected=previous.Trials.Where(t=>t.Status=="PROTOCOL_FAILED").Select(t=>t.Id).ToArray();
            if(selected.Length==0)throw new InvalidOperationException("SUBSCRIPTION_RETRY_FAILURES_MISSING");
        }
        var id=resume?args[1]:"run-"+Guid.NewGuid().ToString("N");var root=Root(report,id);
        var batch=Path.Combine(root,"live","formal");
        var manifest=Path.Combine(batch,"manifest.json");var freeze=Path.Combine(root,"live","freeze-formal.json");
        if(full&&live)RequireFormalGate(stage);
        if(!resume)
        {
            Directory.CreateDirectory(batch);
            ValidationHost.Write(freeze,new{version=1,route=connection,limits,live,fullFormalBatch=full,trialIds=selected,
                previousRunId=retry?args[1]:null,
                previousManifestSha256=previousManifest is null?null:ValidationGate.FileHash(previousManifest),
                artificialCallCeiling=(int?)null,sourceValidationRetained=true,
                driverAssemblySha256=ValidationGate.FileHash(typeof(Program).Assembly.Location),
                understandingAssemblySha256=ValidationGate.FileHash(typeof(UnderstandingService).Assembly.Location),
                kernelAssemblySha256=ValidationGate.FileHash(typeof(ConversationOrchestrator).Assembly.Location),
                dialogueAssemblySha256=ValidationGate.FileHash(typeof(ChatRequestPayloadWriter).Assembly.Location),
                authoritySha256=ValidationGate.FileHash(Path.Combine(stage,"authority.json")),
                originalTrialsSha256=ValidationGate.FileHash(Path.Combine(report,"preregistered-trials.json")),
                originalRepairManifestSha256=ValidationGate.FileHash(Path.Combine(report,"live","repair","manifest.json")),
                capacitySha256=ValidationGate.FileHash(Path.Combine(stage,"model-capacity.json")),
                formalProtocolSha256=full?ValidationGate.FileHash(Path.Combine(stage,"formal-protocol.json")):null,
                engineeringGateSha256=full&&live?ValidationGate.FileHash(Path.Combine(stage,FormalEngineeringGateFile)):null,
                proposalAuthoritySha256=full&&live?ValidationGate.FileHash(Path.Combine(stage,"proposal-isolation-authority.json")):null,
                scope=full?"Original 21 trials, one implementation; isolated synthetic data; no automatic semantic acceptance":
                    "Only selected failed scenarios; original fixtures; isolated synthetic data; no automatic semantic acceptance",
                originalTimeoutsAndParallelConfiguration=true,oldV4Charged=155,semanticAccepted=false});
        }
        var implementation=ValidationGate.FileHash(freeze);
        using(var binding=JsonDocument.Parse(File.ReadAllText(freeze)))
        {
            var b=binding.RootElement;
            if(b.GetProperty("driverAssemblySha256").GetString()!=ValidationGate.FileHash(typeof(Program).Assembly.Location)||
                b.GetProperty("understandingAssemblySha256").GetString()!=ValidationGate.FileHash(typeof(UnderstandingService).Assembly.Location)||
                b.GetProperty("kernelAssemblySha256").GetString()!=ValidationGate.FileHash(typeof(ConversationOrchestrator).Assembly.Location)||
                b.GetProperty("dialogueAssemblySha256").GetString()!=ValidationGate.FileHash(typeof(ChatRequestPayloadWriter).Assembly.Location)||
                b.GetProperty("live").GetBoolean()!=live)
                throw new InvalidOperationException("SUBSCRIPTION_IMPLEMENTATION_CHANGED");
            selected=b.GetProperty("trialIds").EnumerateArray().Select(t=>t.GetString()!).ToArray();
            full=b.TryGetProperty("fullFormalBatch",out var formal)&&formal.GetBoolean();
            if(selected.Length==0||selected.Any(t=>!(full?allIds:FailedIds).Contains(t))||selected.Distinct().Count()!=selected.Length||
                full&&!selected.SequenceEqual(allIds))
                throw new InvalidOperationException("SUBSCRIPTION_FROZEN_TRIALS_INVALID");
            if(full&&live)
            {
                RequireFormalGate(stage);
                if(b.GetProperty("formalProtocolSha256").GetString()!=ValidationGate.FileHash(Path.Combine(stage,"formal-protocol.json"))||
                    b.GetProperty("engineeringGateSha256").GetString()!=ValidationGate.FileHash(Path.Combine(stage,FormalEngineeringGateFile)))
                    throw new InvalidOperationException("SUBSCRIPTION_FORMAL_BINDING_CHANGED");
            }
        }
        var run=resume?ValidationRun.ReadManifest(manifest):new BatchRun{Batch=full?"SubscriptionFormal21":"SubscriptionFailedScenarios",Live=live,Implementation=implementation,
            Trials=selected.Select(t=>new TrialRun{Id=t,Directory=t+"-attempt-1",Implementation=implementation}).ToList()};
        if(run.Implementation!=implementation||!run.Trials.Select(t=>t.Id).SequenceEqual(selected)||
            resume&&!run.Trials.Any(t=>t.Id==args[2]))
            throw new InvalidOperationException("SUBSCRIPTION_MANIFEST_CHANGED");
        ValidationHost.Write(manifest,run);
        using var ledger=new SubscriptionCallLedger(Path.Combine(root,"dispatch-events.jsonl"),
            ReviewBudgetedChatProvider.RouteHash(ModelRouteKey.From(connection)),limits.MaxInputTokens);
        var options=new SubscriptionOptions(connection,limits,ledger);
        foreach(var trial in run.Trials.Where(t=>!resume||t.Id==args[2]))
        {
            if(resume&&trial.Status!="AWAITING_APPROVAL")throw new InvalidOperationException("SUBSCRIPTION_TRIAL_NOT_AWAITING_APPROVAL");
            var fixture=TrialFixtures.Formal().Single(f=>f.Id==trial.Id);
            var directory=Path.Combine(batch,trial.Directory);Directory.CreateDirectory(directory);
            var stateDescriptor=Path.Combine(directory,"state-root.json");
            if(!File.Exists(stateDescriptor))ValidationHost.Write(stateDescriptor,new{container="fgo-p4-buddy",id=id[4..],trial=trial.Id});
            var log=new MetadataLog(Path.Combine(directory,"driver-events.jsonl"));
            var before=ledger.Snapshot().TotalUsed;ValidationHost? host=null;
            try
            {
                trial.Status=resume?"APPROVAL_RESUME_STARTED":"STARTED";trial.Attempts++;
                ValidationHost.Write(manifest,run);log.Event(trial.Id,trial.Status);
                host=await ValidationHost.CreateSubscriptionAsync(repository,directory,fixture,options,live,trial.ClockTicks).ConfigureAwait(false);
                if(resume)
                {
                    if(!ValidationRun.HasExactApproval(host,trial))
                    {trial.Status="AWAITING_APPROVAL";continue;}
                }
                else
                {
                    host.Prepare(fixture);host.SaveEvidence("prepared");
                    foreach(var input in fixture.Inputs)
                    {
                        log.Event(trial.Id,"input_received");RequireReply(await host.SendAsync(input).ConfigureAwait(false));
                        log.Event(trial.Id,"reply_completed");
                    }
                    if(!fixture.SelfSeed)
                    {
                        log.Event(trial.Id,"review_started");await host.ReviewAsync(fixture.External).ConfigureAwait(false);
                        log.Event(trial.Id,"review_returned");host.SaveEvidence("after-review");
                    }
                    if(fixture.Approve)
                    {
                        var pending=host.Owner.List(host.Scope,true);
                        if(pending.Count==0)throw new InvalidOperationException("VALIDATION_APPROVAL_CANDIDATE_MISSING");
                        var packet=Path.Combine(directory,"approval-packet.json");
                        ValidationHost.Write(packet,new ApprovalPacket(trial.Id,host.Scope,host.Clock.Ticks,pending.ToArray()));
                        trial.ApprovalPacketSha256=ValidationGate.FileHash(packet);
                        if(live){trial.Status="AWAITING_APPROVAL";log.Event(trial.Id,"new_exact_version_approval_required");continue;}
                        var chosen=pending[0];host.Owner.Review(host.Scope,"offline-artificial-approval",chosen.Id,chosen.Revision,UnderstandingAction.Approve);
                    }
                }
                if(!fixture.SelfSeed)host.NewSession();
                host.SaveEvidence("before-followup");log.Event(trial.Id,"followup_started");
                RequireReply(await host.SendAsync(fixture.Followup).ConfigureAwait(false));
                if(fixture.SelfSeed)host.CheckExpiry();
                trial.Status="COMPLETED";log.Event(trial.Id,"trial_protocol_completed");
            }
            catch(Exception error)
            {trial.Status="PROTOCOL_FAILED";trial.Code=Program.Safe(error);log.Event(trial.Id,"exception_exit",trial.Code);}
            finally
            {
                if(host is not null)
                {
                    trial.ClockTicks=host.Clock.Ticks;
                    try{host.SaveEvidence(resume?"after-approval-followup":"trial-result");}
                    finally{await host.DisposeAsync().ConfigureAwait(false);}
                }
                trial.ChargedAttempts+=ledger.Snapshot().TotalUsed-before;
                ValidationHost.Write(manifest,run);ValidationHost.Write(Path.Combine(batch,"ledger-snapshot.json"),ledger.Snapshot());
                Console.WriteLine(JsonSerializer.Serialize(new{stage="subscription_trial_recorded",trial.Id,trial.Status,trial.Code,
                    trial.ChargedAttempts,realProviderCalls=live?ledger.Snapshot().TotalUsed:0,semanticAccepted=false}));
            }
        }
        ValidationRun.WriteReviewPacket(batch,run);
        Console.WriteLine(JsonSerializer.Serialize(new{stage="subscription_run_recorded",runId=id,live,
            completed=run.Trials.Count(t=>t.Status=="COMPLETED"),awaiting=run.Trials.Count(t=>t.Status=="AWAITING_APPROVAL"),
            failed=run.Trials.Count(t=>t.Status=="PROTOCOL_FAILED"),charged=ledger.Snapshot().TotalUsed,semanticAccepted=false}));
        return run.Trials.Any(t=>t.Status=="PROTOCOL_FAILED")?1:run.Trials.Any(t=>t.Status=="AWAITING_APPROVAL")?3:0;
    }
    private static void RequireReply(ConversationSendResult reply)
    {if(reply.Status!=ConversationSendStatus.Completed)throw new InvalidOperationException("VALIDATION_REPLY_INCOMPLETE");}
    private static void RequireFormalGate(string stage)
    {
        using var protocol=JsonDocument.Parse(File.ReadAllText(Path.Combine(stage,"formal-protocol.json")));
        using var gate=JsonDocument.Parse(File.ReadAllText(Path.Combine(stage,FormalEngineeringGateFile)));
        if(!protocol.RootElement.GetProperty("userAuthorizedFull21").GetBoolean()||
            !gate.RootElement.GetProperty("coveredEngineeringChecksPassed").GetBoolean()||
            gate.RootElement.GetProperty("driverAssemblySha256").GetString()!=ValidationGate.FileHash(typeof(Program).Assembly.Location)||
            gate.RootElement.GetProperty("kernelAssemblySha256").GetString()!=ValidationGate.FileHash(typeof(ConversationOrchestrator).Assembly.Location)||
            gate.RootElement.GetProperty("dialogueAssemblySha256").GetString()!=ValidationGate.FileHash(typeof(ChatRequestPayloadWriter).Assembly.Location)||
            gate.RootElement.GetProperty("understandingAssemblySha256").GetString()!=ValidationGate.FileHash(typeof(UnderstandingService).Assembly.Location)||
            gate.RootElement.GetProperty("proposalAuthoritySha256").GetString()!=ValidationGate.FileHash(Path.Combine(stage,"proposal-isolation-authority.json"))||
            gate.RootElement.GetProperty("protocolSha256").GetString()!=ValidationGate.FileHash(Path.Combine(stage,"formal-protocol.json")))
            throw new InvalidOperationException("SUBSCRIPTION_FORMAL_ENGINEERING_GATE_REQUIRED");
    }
    private static void CheckAccounting(string stage,ModelConnectionSettings connection,ReviewRequestLimits limits)
    {
        var path=Path.Combine(stage,"accounting-self-check-"+Guid.NewGuid().ToString("N")+".jsonl");
        var route=ReviewBudgetedChatProvider.RouteHash(ModelRouteKey.From(connection));
        using(var ledger=new SubscriptionCallLedger(path,route,limits.MaxInputTokens))
        {
            ledger.Register("R2-2","synthetic");
            var plan=new ReviewCallPlan(ReviewBatch.Formal,"R2-2","synthetic","one",route,"reply",UnderstandingMode.Proposals,1,new('A',64),20000);
            if(ledger.TryReserve(plan with{RouteHash=new('B',64)})is not null||ledger.TryReserve(plan with{ScopeKey="wrong"})is not null)
                throw new InvalidOperationException("SUBSCRIPTION_ACCOUNTING_SCOPE_FAILURE");
            for(var i=0;i<260;i++)if(ledger.TryReserve(plan with{RequestId="request-"+i})is null)
                throw new InvalidOperationException("SUBSCRIPTION_ACCOUNTING_ARTIFICIAL_CAP");
            var first=ledger.Snapshot().Calls[0];ledger.Complete(first.RequestHash,ReviewCallOutcome.Unknown);
            if(ledger.TryReserve(plan with{RequestId="request-0"})is not null||ledger.Snapshot().TotalUsed!=260)
                throw new InvalidOperationException("SUBSCRIPTION_ACCOUNTING_REPLAY_FAILURE");
        }
        using var reopened=new SubscriptionCallLedger(path,route,limits.MaxInputTokens);
        if(reopened.Snapshot().TotalUsed!=260||reopened.Snapshot().Calls[0].Outcome!=ReviewCallOutcome.Unknown)
            throw new InvalidOperationException("SUBSCRIPTION_ACCOUNTING_PERSISTENCE_FAILURE");
        Console.WriteLine(JsonSerializer.Serialize(new{stage="subscription_accounting_checked",reserved=260,realProviderCalls=0,
            scopeAndRouteDenied=true,replayDenied=true,unknownRetained=true}));
    }
}
