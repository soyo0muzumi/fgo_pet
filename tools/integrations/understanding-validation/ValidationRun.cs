using System.IO;
using System.Text;
using System.Text.Json;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Settings;
using FgoPet.Understanding;
using FgoPet.Understanding.Contracts;

namespace FgoPet.Testing.Understanding;

internal sealed class TrialRun
{
    public string Id {get;set;}="";
    public string Status {get;set;}="NOT_STARTED";
    public string? Code {get;set;}
    public string Directory {get;set;}="";
    public string Implementation {get;set;}="";
    public long ClockTicks {get;set;}
    public int Attempts {get;set;}
    public int ChargedAttempts {get;set;}
    public int SmokeReviewRepairs {get;set;}
    public string? ApprovalPacketSha256 {get;set;}
    public List<object> PriorSmokeAttempts {get;set;}=[];
}
internal sealed class BatchRun
{
    public int Schema {get;set;}=1;
    public string Batch {get;set;}="";
    public string Implementation {get;set;}="";
    public bool Live {get;set;}
    public bool SemanticAcceptance {get;set;}
    public List<TrialRun> Trials {get;set;}=[];
}

internal static class ValidationRun
{
    internal static BatchRun ReadManifest(string path)=>JsonSerializer.Deserialize<BatchRun>(File.ReadAllText(path),ValidationHost.Json)
        ??throw new InvalidOperationException("VALIDATION_MANIFEST_INVALID");
    internal static async Task<int> RunAsync(string repository,string root,ReviewBatch batch,ReviewCallLedger ledger,
        ModelConnectionSettings connection,bool live,string implementation,Func<ModelRouteKey,IRequestTokenMeter>? meterFactory=null)
    {
        var smoke=batch==ReviewBatch.Smoke;var fixtures=smoke?TrialFixtures.Smoke():TrialFixtures.Formal();
        var batchDirectory=Path.Combine(root,batch.ToString().ToLowerInvariant());Directory.CreateDirectory(batchDirectory);
        var manifestPath=Path.Combine(batchDirectory,"manifest.json");
        var run=File.Exists(manifestPath)?ReadManifest(manifestPath):new BatchRun{Batch=batch.ToString(),Live=live,Implementation=implementation,
            Trials=fixtures.Select(f=>new TrialRun{Id=f.Id}).ToList()};
        if(run.Schema!=1||run.Live!=live||run.Batch!=batch.ToString()||run.SemanticAcceptance||
            !run.Trials.Select(t=>t.Id).SequenceEqual(fixtures.Select(f=>f.Id))||!smoke&&run.Implementation!=implementation)
            throw new InvalidOperationException("VALIDATION_BATCH_IMMUTABLE");
        ValidationHost.Write(manifestPath,run);
        foreach(var fixture in fixtures)
        {
            var trial=run.Trials.Single(t=>t.Id==fixture.Id);
            if(smoke&&trial.Status=="COMPLETED"&&trial.Implementation==implementation)continue;
            if(!smoke&&trial.Status is "COMPLETED"or"PROTOCOL_FAILED"or"UNKNOWN_STOP")continue;
            if(!smoke&&trial.Status is not("NOT_STARTED"or"AWAITING_APPROVAL"))
            {trial.Status="UNKNOWN_STOP";trial.Code="VALIDATION_INTERRUPTED_ATTEMPT_NOT_REPLAYED";ValidationHost.Write(manifestPath,run);continue;}
            var resume=trial.Status=="AWAITING_APPROVAL";
            if(resume&&(!trial.Directory.StartsWith(fixture.Id+"-attempt-",StringComparison.Ordinal)||Path.GetFileName(trial.Directory)!=trial.Directory))
                throw new InvalidOperationException("VALIDATION_TRIAL_PATH_INVALID");
            if(smoke&&trial.Attempts>0)
                trial.PriorSmokeAttempts.Add(new{trial.Directory,trial.Status,trial.Code,trial.Implementation,trial.ChargedAttempts});
            if(!resume)
            {
                trial.Attempts++;trial.Directory=fixture.Id+"-attempt-"+trial.Attempts;
                if(System.IO.Directory.Exists(Path.Combine(batchDirectory,trial.Directory)))throw new InvalidOperationException("VALIDATION_TRIAL_ALREADY_EXISTS");
                trial.ClockTicks=0;trial.Implementation=implementation;trial.Code=null;trial.Status="STARTED";
                ValidationHost.Write(manifestPath,run);
            }
            var directory=Path.Combine(batchDirectory,trial.Directory);
            var log=new MetadataLog(Path.Combine(directory,"driver-events.jsonl"));
            System.IO.Directory.CreateDirectory(directory);
            var before=ledger.Snapshot().TotalUsed;ValidationHost? host=null;
            try
            {
                log.Event(fixture.Id,resume?"resume_for_approved_followup":"trial_start");
                host=await ValidationHost.CreateAsync(repository,directory,fixture,batch,ledger,connection,live,trial.ClockTicks,meterFactory:meterFactory).ConfigureAwait(false);
                if(resume)
                {
                    if(!HasExactApproval(host,trial))continue;
                }
                else
                {
                    host.Prepare(fixture);host.SaveEvidence("prepared");
                    if(fixture.Mode==UnderstandingMode.Off)
                    {await host.OffPreviewAsync("smoke-explicit-preview").ConfigureAwait(false);trial.Status="COMPLETED";continue;}
                    foreach(var input in fixture.Inputs)
                    {
                        log.Event(fixture.Id,"input_received");
                        RequireReply(await host.SendAsync(input).ConfigureAwait(false));
                        log.Event(fixture.Id,"reply_completed");
                    }
                    if(!fixture.SelfSeed)
                    {
                        log.Event(fixture.Id,"review_started");await host.ReviewAsync(fixture.External).ConfigureAwait(false);
                        log.Event(fixture.Id,"review_returned");host.SaveEvidence("after-review");
                    }
                    if(fixture.Approve)
                    {
                        var pending=host.Owner.List(host.Scope,true);
                        if(pending.Count==0)throw new InvalidOperationException("VALIDATION_APPROVAL_CANDIDATE_MISSING");
                        var packet=new ApprovalPacket(fixture.Id,host.Scope,host.Clock.Ticks,pending.ToArray());
                        var packetPath=Path.Combine(directory,"approval-packet.json");ValidationHost.Write(packetPath,packet);
                        trial.ApprovalPacketSha256=ValidationGate.FileHash(packetPath);trial.Status="AWAITING_APPROVAL";
                        log.Event(fixture.Id,"exact_version_human_approval_required");
                        // Offline protocol runs use an explicitly artificial operator; never a semantic pass.
                        if(!live)
                        {
                            var chosen=pending[0];var receipt=host.Owner.Review(host.Scope,"offline-artificial-approval",chosen.Id,chosen.Revision,UnderstandingAction.Approve);
                            if(receipt.Items.Any(i=>i.Outcome!=UnderstandingOutcome.Applied))throw new InvalidOperationException("VALIDATION_ARTIFICIAL_APPROVAL_FAILED");
                            ValidationHost.Write(Path.Combine(directory,"offline-artificial-approval.json"),new{actor="offline test operator",receipt,semanticAccepted=false});
                        }
                        else continue;
                    }
                }
                trial.Status="FOLLOWUP_STARTED";trial.ClockTicks=host.Clock.Ticks;ValidationHost.Write(manifestPath,run);
                if(!fixture.SelfSeed)host.NewSession();
                host.SaveEvidence("before-followup");log.Event(fixture.Id,"followup_started");
                RequireReply(await host.SendAsync(fixture.Followup).ConfigureAwait(false));
                if(fixture.SelfSeed)host.CheckExpiry();
                trial.Status="COMPLETED";log.Event(fixture.Id,"trial_protocol_completed");
            }
            catch(Exception error)
            {trial.Status="PROTOCOL_FAILED";trial.Code=Program.Safe(error);log.Event(fixture.Id,"exception_exit",trial.Code);}
            finally
            {
                if(host is not null)
                {
                    trial.ClockTicks=host.Clock.Ticks;
                    try{host.SaveEvidence(resume?"after-approval-followup":"trial-result");}
                    catch(Exception){trial.Status="PROTOCOL_FAILED";trial.Code="VALIDATION_EVIDENCE_WRITE_FAILED";}
                    await host.DisposeAsync().ConfigureAwait(false);
                }
                trial.ChargedAttempts+=ledger.Snapshot().TotalUsed-before;
                ValidationHost.Write(manifestPath,run);
                ValidationHost.Write(Path.Combine(batchDirectory,"ledger-snapshot.json"),ledger.Snapshot());
                WriteReviewPacket(batchDirectory,run);
            }
        }
        Console.WriteLine(JsonSerializer.Serialize(new{status="BATCH_RECORDED",batch,live,chargedAttempts=ledger.Snapshot().TotalUsed,
            completed=run.Trials.Count(t=>t.Status=="COMPLETED"),awaitingApproval=run.Trials.Count(t=>t.Status=="AWAITING_APPROVAL"),
            failed=run.Trials.Count(t=>t.Status is "PROTOCOL_FAILED"or"UNKNOWN_STOP"),semanticAccepted=false}));
        return run.Trials.Any(t=>t.Status is "PROTOCOL_FAILED"or"UNKNOWN_STOP")?1:run.Trials.Any(t=>t.Status=="AWAITING_APPROVAL")?3:0;
    }
    private static void RequireReply(ConversationSendResult result)
    {if(result.Status!=ConversationSendStatus.Completed)throw new InvalidOperationException("VALIDATION_REPLY_INCOMPLETE");}
    internal static string EntryMeaning(UnderstandingEntry entry)=>ValidationHost.Digest(JsonSerializer.Serialize(new{
        entry.Id,entry.Scope,entry.Domain,entry.Kind,entry.Subject,entry.Content,entry.Conditions,entry.BasisKind,entry.Sources,
        entry.ObservedAt,entry.ActiveUntil,entry.ReplacesId,entry.ReplacesRevision,entry.Correction,entry.Coverage}));
    internal static bool HasExactApproval(ValidationHost host,TrialRun trial)
    {
        var packetPath=Path.Combine(host.DirectoryPath,"approval-packet.json");
        if(ValidationGate.FileHash(packetPath)!=trial.ApprovalPacketSha256)throw new InvalidOperationException("VALIDATION_APPROVAL_PACKET_CHANGED");
        var proofPath=Path.Combine(host.DirectoryPath,"ui-review-result.json");if(!File.Exists(proofPath))return false;
        using var proof=JsonDocument.Parse(File.ReadAllText(proofPath));
        if(proof.RootElement.GetProperty("packetSha256").GetString()!=trial.ApprovalPacketSha256||
            !proof.RootElement.GetProperty("actualUi").GetBoolean()||
            proof.RootElement.GetProperty("batchFreezeSha256").GetString()!=trial.Implementation||
            proof.RootElement.GetProperty("uiHostAssemblySha256").GetString()!=ValidationGate.FileHash(typeof(Program).Assembly.Location))
            throw new InvalidOperationException("VALIDATION_HUMAN_REVIEW_EVIDENCE_INVALID");
        var approved=proof.RootElement.GetProperty("approved").EnumerateArray().ToArray();
        if(approved.Length==0&&proof.RootElement.GetProperty("allCandidatesExplicitlyRejected").GetBoolean())
            throw new InvalidOperationException("VALIDATION_HUMAN_DECLINED_ALL_CANDIDATES");
        var current=host.Owner.Query(host.Scope,"").Items;
        return approved.Any(a=>current.Any(e=>e.Id==a.GetProperty("Id").GetString()&&e.Revision==a.GetProperty("Revision").GetInt32()&&
            e.Review==UnderstandingReview.Approved&&EntryMeaning(e)==a.GetProperty("meaningSha256").GetString()));
    }
    internal static void WriteReviewPacket(string directory,BatchRun run)
    {
        var text=new StringBuilder("# P4 人工检查包\n\n所有内容为合成隔离试验。协议完成不等于语义通过，P4 / H2 尚未签核。\n\n");
        text.AppendLine("| Trial | 协议状态 | 本 trial 已扣请求 | 证据 |\n|---|---|---:|---|");
        foreach(var t in run.Trials)text.AppendLine($"| {t.Id} | {t.Status} | {t.ChargedAttempts} | [{t.Directory}]({t.Directory}/) |");
        text.AppendLine("\n按仓库验收规约 v4 检查全部关键证据，在 review-template.json 的每个检查项填写 yes / no / unclear 和证据位置。仅 R3A 合法无产物的依赖项可按规约填写 N/A。不得把异常、超时或解析失败当成 no_change。\n");
        text.AppendLine("R1/R4 的 approval-packet.json 是具体候选；必须由人工确认语义合理后在实际 Understanding 界面批准精确版本。界面操作记录也不替代全批语义评分或最终 H2 签核。\n");
        File.WriteAllText(Path.Combine(directory,"REVIEW.md"),text.ToString());
        var template=Path.Combine(directory,"review-template.json");
        if(!File.Exists(template))ValidationHost.Write(template,new{reviewer=(string?)null,reviewedByHuman=false,h2Signed=false,qualityLimitedAcceptance=(string?)null,
            trials=run.Trials.Select(t=>new{t.Id,hard=new Dictionary<string,object?>{
                ["scope-and-original-attribution"]=null,["exact-source-and-target-version"]=null,["no-pending-user-relationship-leak"]=null,
                ["no-unconditional-upgrade-or-fabricated-experience"]=null,["exact-approval-and-policy-state"]=null,
                ["actual-next-session-input-provenance"]=null,["trial-batch-total-accounting"]=null,["scenario-specific-v4-hard-items"]=null},
                quality=new Dictionary<string,object?>{["natural-expression"]=null,["relevance"]=null,["appropriate-tone"]=null,["topic-switch-without-mechanical-insertion"]=null},
                r3a=t.Id.StartsWith("R3A-",StringComparison.Ordinal)?new{formed=(string?)null,persisted=(string?)null,retrieved=(string?)null,used=(string?)null,lawfulNoChange=(bool?)null}:null,
                evidence=(string?)null,notes=(string?)null})});
    }
}
internal sealed record ApprovalPacket(string Trial,ConversationScope Scope,long ClockTicks,UnderstandingEntry[] Candidates);
