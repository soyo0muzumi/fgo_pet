using System.IO;
using System.Text.Json;
using System.Windows;
using FgoPet.App.Dialogue;
using FgoPet.App.Settings;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Understanding;
using FgoPet.Understanding.Contracts;
using FgoPet.Understanding.Desktop;
using FgoPet.UiSdk;

namespace FgoPet.Testing.Understanding;

// This host intentionally does not compose providers, credentials, HTTP or a review worker.
internal static class HumanReviewWindow
{
    internal static int Run(string report,string trialId,bool repair)
    {
        var batch=Path.Combine(report,"live",repair?"repair":"formal");
        var run=ValidationRun.ReadManifest(Path.Combine(batch,"manifest.json"));
        var trial=run.Trials.SingleOrDefault(t=>t.Id==trialId)??throw new InvalidOperationException("VALIDATION_TRIAL_UNKNOWN");
        if(trial.Status!="AWAITING_APPROVAL")throw new InvalidOperationException("VALIDATION_TRIAL_NOT_AWAITING_APPROVAL");
        var directory=Path.GetFullPath(Path.Combine(batch,trial.Directory));
        if(!directory.StartsWith(Path.GetFullPath(batch)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("VALIDATION_TRIAL_PATH_INVALID");
        var packetPath=Path.Combine(directory,"approval-packet.json");
        if(ValidationGate.FileHash(packetPath)!=trial.ApprovalPacketSha256)throw new InvalidOperationException("VALIDATION_APPROVAL_PACKET_CHANGED");
        var packet=JsonSerializer.Deserialize<ApprovalPacket>(File.ReadAllText(packetPath),ValidationHost.Json)
            ??throw new InvalidOperationException("VALIDATION_APPROVAL_PACKET_INVALID");
        if(packet.Trial!=trialId)throw new InvalidOperationException("VALIDATION_APPROVAL_PACKET_INVALID");
        var previousRoot=Environment.GetEnvironmentVariable("FGO_PET_STATE_ROOT");
        var stateDirectory=ValidationHost.StateDirectory(directory);
        Environment.SetEnvironmentVariable("FGO_PET_STATE_ROOT",stateDirectory);
        try
        {
            var database=new RuntimeDatabase(Path.Combine(stateDirectory,"runtime.db"),pooling:false);
            var raw=new SqliteExperienceSourceReader(database);
            var timeline=new FixtureSources(new SqliteExperienceTimeline(database,raw),raw,packet.Scope,false);
            var clock=new ValidationHost.ControlledClock{Ticks=packet.ClockTicks};
            var log=new MetadataLog(Path.Combine(directory,"ui-events.jsonl"));
            var owner=new UnderstandingService(database,timeline,clock,diagnostics:d=>log.Event(d.Correlation,d.Stage,d.Code));
            var before=owner.List(packet.Scope,true);
            if(packet.Candidates.Any(e=>!before.Any(p=>p.Id==e.Id&&p.Revision==e.Revision&&ValidationRun.EntryMeaning(p)==ValidationRun.EntryMeaning(e))))
                throw new InvalidOperationException("VALIDATION_APPROVAL_VERSION_CHANGED");
            var page=new UnderstandingWebPage(owner,()=>packet.Scope,new DialogueContextLifetime(),timeline);
            var catalog=new SettingsPageCatalog([new SettingsPageViewFactory("Understanding",
                _=>throw new InvalidOperationException("SETTINGS_USE_WEB_ROOT"),"持续理解",group:"能力",order:145)]);
            var viewModel=new SettingsViewModel();viewModel.Navigate("Understanding");
            var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
            using var window=new SettingsWindow(viewModel,new SettingsWebRootFactory(catalog,[page],stateDirectory));
            window.Title="P4 合成隔离验收 · "+trialId+" · 关闭后保存实际审批结果";
            var closing=false;var ready=false;var failed=false;
            var surface=(WebView2SurfaceHost)window.Content;
            surface.Ready+=(_,_)=>{ready=true;log.Event(trialId,"actual_review_ui_ready");};
            window.IsVisibleChanged+=(_,_)=>
            {
                if(window.IsVisible||closing)return;
                closing=true;log.Event(trialId,"actual_review_ui_close_requested");
                // SettingsWindow hides inside Closing; disposal must wait for that callback to return.
                app.Dispatcher.BeginInvoke(new Action(()=>
                {
                    log.Event(trialId,"actual_review_ui_close_dispatched");window.Dispose();app.Shutdown();
                }));
            };
            app.DispatcherUnhandledException+=(_,e)=>{failed=true;e.Handled=true;log.Event(trialId,"ui_exception","VALIDATION_UI_FAILED");
                Console.WriteLine(JsonSerializer.Serialize(new{stage="ui_exception",type=e.Exception.GetType().Name,
                    hresult=e.Exception.HResult,callsites=new System.Diagnostics.StackTrace(e.Exception,false).GetFrames()
                        .Take(5).Select(frame=>frame.GetMethod()?.Name).ToArray()}));app.Shutdown();};
            log.Event(trialId,"actual_review_ui_opened");
            window.Show();app.Run();
            var current=owner.Query(packet.Scope,"").Items;
            var approved=packet.Candidates.Select(c=>new{candidate=c,current=current.SingleOrDefault(e=>e.Id==c.Id)})
                .Where(x=>x.current is{} e&&e.Review==UnderstandingReview.Approved&&e.Effect==UnderstandingEffect.Approved&&
                    e.Revision==x.candidate.Revision+1&&ValidationRun.EntryMeaning(e)==ValidationRun.EntryMeaning(x.candidate))
                .Select(x=>new{x.current!.Id,x.current.Revision,meaningSha256=ValidationRun.EntryMeaning(x.current),approvedFromRevision=x.candidate.Revision}).ToArray();
            var all=owner.List(packet.Scope,true).Concat(owner.List(packet.Scope,false)).ToArray();
            var rejected=packet.Candidates.Where(c=>all.Any(e=>e.Id==c.Id&&e.Revision==c.Revision+1&&
                e.Review==UnderstandingReview.Rejected&&ValidationRun.EntryMeaning(e)==ValidationRun.EntryMeaning(c)))
                .Select(c=>new{c.Id,reviewedRevision=c.Revision}).ToArray();
            var result=new{atUtc=DateTimeOffset.UtcNow,packetSha256=trial.ApprovalPacketSha256,actualUi=ready&&!failed,
                trial=trialId,actor="operator of the visible actual Understanding page",approved,rejected,
                uiHostAssemblySha256=ValidationGate.FileHash(typeof(Program).Assembly.Location),
                batchFreezeSha256=ValidationGate.FileHash(Path.Combine(report,"live",repair?"freeze-repair.json":"freeze-formal.json")),
                uiHostShutdown="dispatcher_deferred",
                allCandidatesExplicitlyRejected=rejected.Length==packet.Candidates.Length,
                semanticAcceptanceByHumanStillRequired=true,realProviderCalls=0,defaultStateAccess=false};
            ValidationHost.Write(Path.Combine(directory,"ui-review-"+Guid.NewGuid().ToString("N")+".json"),result);
            ValidationHost.Write(Path.Combine(directory,"ui-review-result.json"),result);
            Console.WriteLine(JsonSerializer.Serialize(new{status="ACTUAL_UI_REVIEW_RECORDED",trial=trialId,approved=approved.Length,realProviderCalls=0,semanticAccepted=false}));
            return failed?1:0;
        }
        finally{Environment.SetEnvironmentVariable("FGO_PET_STATE_ROOT",previousRoot);}
    }
}
