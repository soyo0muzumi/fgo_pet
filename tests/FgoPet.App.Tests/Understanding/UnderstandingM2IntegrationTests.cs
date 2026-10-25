using System.Net;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using FgoPet.App.Bootstrap;
using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Settings;
using FgoPet.Dialogue.Settings;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Understanding;
using FgoPet.Understanding.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace FgoPet.App.Tests.Understanding;

[Collection("H1 full catalog isolation")]
public sealed class UnderstandingM2IntegrationTests
{
    [Theory][InlineData(false)][InlineData(true)]
    public async Task Daily_exhaustion_between_review_and_classification_preserves_work_until_allowance_recovers(bool nextDay)
    {
        await using var host=new Host(approved:false);await host.StartAsync();
        var access=host.Provider.GetRequiredService<DailyReviewBudgetAccess>();
        var ledger=host.Provider.GetRequiredService<DailyReviewLedger>();
        var scheduler=host.Provider.GetRequiredService<UnderstandingReviewScheduler>();
        var route=access.Status(host.Scope).RouteHash!;
        access.Configure(host.Scope,0,new(UnderstandingMode.Bounded,ReadSources:true,ExternalProcessing:true,SaveCandidates:true,UseInDialogue:true),
            host.Provider.GetRequiredService<IExperienceTimeline>().Boundary(host.Scope),route,2,default);
        // An earlier unknown dispatch remains charged, leaving only the review call's allowance.
        var earlier=Assert.IsType<ReviewCallReservation>(ledger.TryReserve(new(ReviewBatch.Formal,"daily",host.Scope.Key,"earlier-unknown",
            route,"review",UnderstandingMode.Bounded,host.Owner.State(host.Scope).Generation,new('A',64),1000)));
        ledger.Complete(earlier.RequestHash,ReviewCallOutcome.Unknown);
        host.Append();host.Clock.Advance(120);await scheduler.PulseAsync();

        Assert.Equal(1,host.Http.Sends);Assert.Equal(2,access.Status(host.Scope).Budget!.Used);
        Assert.Equal("UNDERSTANDING_BUDGET_LIMITED",access.Status(host.Scope).Code);
        Assert.Equal(2,host.Owner.WorkSummary(host.Scope).PendingSources);
        Assert.DoesNotContain(host.Owner.WorkSummary(host.Scope).Batches,b=>b.State==ReviewWorkState.Failed);
        Assert.Empty(host.Owner.Query(host.Scope,"").Items);Assert.Empty(host.Owner.List(host.Scope,true));
        host.Clock.Advance(300);await scheduler.PulseAsync();
        Assert.Equal(1,host.Http.Sends);Assert.Equal(2,access.Status(host.Scope).Budget!.Used);
        Assert.Equal(2,host.Owner.WorkSummary(host.Scope).PendingSources);

        if(nextDay)host.Clock.Advance(86400);
        else
        {
            var state=host.Owner.State(host.Scope);
            access.Configure(host.Scope,state.Revision,state.Policy,host.Provider.GetRequiredService<IExperienceTimeline>().Boundary(host.Scope),route,4,default);
            host.Clock.Advance(300);
        }
        await scheduler.PulseAsync();
        Assert.Equal(3,host.Http.Sends);
        Assert.Equal(nextDay?2:4,access.Status(host.Scope).Budget!.Used);
        Assert.Equal(0,host.Owner.WorkSummary(host.Scope).PendingSources);
        Assert.DoesNotContain(host.Owner.WorkSummary(host.Scope).Batches,b=>b.State==ReviewWorkState.Failed);
        Assert.Equal(UnderstandingEffect.Bounded,Assert.Single(host.Owner.Query(host.Scope,"").Items).Effect);
    }
    [Theory][InlineData(UnderstandingMode.Proposals,1)][InlineData(UnderstandingMode.Bounded,2)]
    public async Task Daily_host_runs_the_production_pipeline_and_only_bounded_results_enter_replies(UnderstandingMode mode,int calls)
    {
        await using var host=new Host(approved:false);await host.StartAsync();
        var access=host.Provider.GetRequiredService<DailyReviewBudgetAccess>();
        access.Configure(host.Scope,0,new(mode,ReadSources:true,ExternalProcessing:true,SaveCandidates:true,UseInDialogue:true),
            host.Provider.GetRequiredService<IExperienceTimeline>().Boundary(host.Scope),access.Status(host.Scope).RouteHash!,12,default);
        host.Append();host.Clock.Advance(120);
        await host.Provider.GetRequiredService<UnderstandingReviewScheduler>().PulseAsync();
        Assert.Equal(calls,host.Http.Sends);Assert.Equal(calls,access.Status(host.Scope).Budget!.Used);
        Assert.Equal(mode==UnderstandingMode.Bounded?1:0,host.Owner.Query(host.Scope,"").Items.Count);
        Assert.Equal(mode==UnderstandingMode.Proposals?1:0,host.Owner.List(host.Scope,true).Count);
        host.Owner.Configure(host.Scope,host.Owner.State(host.Scope).Revision,host.Owner.State(host.Scope).Policy with{UseInDialogue=false});
        Assert.Empty(host.Owner.Query(host.Scope,"").Items);
    }
    [Fact] public async Task App_registration_uses_real_worker_adapter_http_ledger_and_bounded_projection()
    {
        await using var host=new Host(approved:true);await host.StartAsync();host.Enable();host.Append();
        host.Clock.Advance(120);await host.Provider.GetRequiredService<UnderstandingReviewScheduler>().PulseAsync();
        Assert.Equal(2,host.Http.Sends); // One Review plus one independent semantic classification, both synthetic HTTP.
        var calls=host.Ledger.Snapshot();Assert.Equal(2,calls.TotalUsed);
        Assert.Equal(new[]{"review","classify"},calls.Calls.Select(c=>c.Purpose));Assert.All(calls.Calls,c=>Assert.Equal(ReviewCallOutcome.Completed,c.Outcome));
        var entry=Assert.Single(host.Owner.Query(host.Scope,"").Items);Assert.Equal(UnderstandingEffect.Bounded,entry.Effect);
        Assert.Equal(UnderstandingReview.Unreviewed,entry.Review);Assert.NotEmpty(entry.Coverage!);
        var plugin=Assert.IsType<UnderstandingPlugin>(host.Plugin);
        Assert.Contains(plugin.BuildPrompt(new("c","mash","p"),"Synthetic",false),b=>b.Text.Contains(entry.Id,StringComparison.Ordinal));
        await host.Provider.GetRequiredService<UnderstandingReviewScheduler>().PulseAsync();Assert.Equal(2,host.Http.Sends);
    }
    [Fact] public async Task App_default_budget_denies_http_even_when_automatic_policy_and_threshold_are_satisfied()
    {
        await using var host=new Host(approved:false);await host.StartAsync();host.Enable();host.Append();
        host.Clock.Advance(120);await host.Provider.GetRequiredService<UnderstandingReviewScheduler>().PulseAsync();
        Assert.Equal(0,host.Http.Sends);Assert.Equal(0,host.Credentials.Reads);Assert.Equal(0,host.Ledger.Snapshot().TotalUsed);
        Assert.Equal(2,host.Owner.WorkSummary(host.Scope).PendingSources);
    }
    [Fact] public async Task Revocation_inside_real_async_credential_read_prevents_both_precharge_and_http()
    {
        await using var host=new Host(approved:true);await host.StartAsync();host.Enable();host.Append();
        host.Credentials.Before=()=>host.Owner.Configure(host.Scope,host.Owner.State(host.Scope).Revision,new());
        host.Clock.Advance(120);await host.Provider.GetRequiredService<UnderstandingReviewScheduler>().PulseAsync();
        Assert.Equal(1,host.Credentials.Reads);Assert.Equal(0,host.Http.Sends);Assert.Equal(0,host.Ledger.Snapshot().TotalUsed);
        Assert.Empty(host.Owner.List(host.Scope,true));Assert.Empty(host.Owner.List(host.Scope,false));
    }
    [Fact] public async Task Stopping_the_registered_worker_drains_its_http_and_denies_later_pulses()
    {
        await using var host=new Host(approved:true);var plugin=host.Plugin;
        host.Enable();host.Append();host.Http.WaitForCancellation=true;host.Clock.Advance(120);
        await plugin.StartAsync(default);await host.Http.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await plugin.StopAsync(default).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1,host.Http.Sends);Assert.Equal(ReviewCallOutcome.Unknown,Assert.Single(host.Ledger.Snapshot().Calls).Outcome);
        Assert.Equal(2,host.Owner.WorkSummary(host.Scope).PendingSources);
        host.Http.WaitForCancellation=false;host.Clock.Advance(120);
        await host.Provider.GetRequiredService<UnderstandingReviewScheduler>().PulseAsync();
        Assert.Equal(1,host.Http.Sends);
    }
    private sealed class Host:IAsyncDisposable
    {
        private readonly string? _oldRoot=Environment.GetEnvironmentVariable("FGO_PET_STATE_ROOT");
        private readonly RuntimeDatabase _database;
        public readonly ConversationScope Scope=new("mash","p");public readonly Clock Clock=new();
        public readonly SyntheticHttp Http=new();public readonly Credentials Credentials=new();
        public readonly ReviewCallLedger Ledger;public readonly ServiceProvider Provider;
        public UnderstandingService Owner=>Provider.GetRequiredService<UnderstandingService>();
        public IFgoPetPlugin Plugin=>Provider.GetRequiredService<PluginCatalog>().Plugins.Single(p=>p.Manifest.Id=="firstparty.understanding").Instance;
        public Host(bool approved)
        {
            var root=Path.Combine(Path.GetTempPath(),"fgo-app-m2-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            Environment.SetEnvironmentVariable("FGO_PET_STATE_ROOT",root);
            _database=new(Path.Combine(root,"runtime.db"),pooling:false);new RuntimeDatabaseMigrator(_database).Migrate();
            Ledger=new(Path.Combine(root,"separate-ledger.db"));var settings=new Settings();
            Ledger.Approve(new("synthetic-only",ReviewBudgetedChatProvider.RouteHash(ModelRouteKey.From(settings.Load().ModelConnection!)),new('A',64),true));
            Ledger.RegisterTrial(ReviewBatch.Formal,"R3A-1",Scope.Key,6);
            var services=new ServiceCollection().AddFgoPet([]);services.Replace(ServiceDescriptor.Singleton(_database));
            services.Replace(ServiceDescriptor.Singleton<TimeProvider>(Clock));services.Replace(ServiceDescriptor.Singleton<IDialogueSettingsStore>(settings));
            services.Replace(ServiceDescriptor.Singleton(new HttpClient(Http)));
            services.Replace(ServiceDescriptor.Singleton<FgoPet.Infrastructure.Secrets.ICredentialReader>(Credentials));
            services.Replace(ServiceDescriptor.Singleton<FgoPet.Core.Secrets.ICredentialReader>(Credentials));
            services.Replace(ServiceDescriptor.Singleton<IConversationScopeAuthority>(new Authority(Scope)));
            if(approved)services.Replace(ServiceDescriptor.Singleton<IReviewBudgetAccess>(new Access(new(Ledger,ReviewBatch.Formal,"R3A-1",Scope.Key))));
            Provider=services.BuildServiceProvider();
        }
        public async Task StartAsync()=>await Plugin.StartAsync(default);
        public void Enable()=>Owner.Configure(Scope,0,new(UnderstandingMode.Bounded,ReadSources:true,ExternalProcessing:true,SaveCandidates:true,UseInDialogue:true),
            Provider.GetRequiredService<IExperienceTimeline>().Boundary(Scope));
        public void Append()
        {
            var repository=Provider.GetRequiredService<SqliteConversationRepository>();var context=new ContentContextKey("mash","fixture","1.0.0","default","p1","k1");
            repository.CreateConversation("c","mash",context,Clock.GetUtcNow(),"p","Synthetic");
            repository.Append(new("u","c","mash",ChatMessageRole.User,"Synthetic story about listening.",ChatMessageStatus.Completed,Clock.GetUtcNow(),context,1));
            repository.Append(new("a","c","mash",ChatMessageRole.Assistant,"Today I am curious about listening before agreement.",ChatMessageStatus.Completed,Clock.GetUtcNow(),context,2));
        }
        public async ValueTask DisposeAsync()
        {try{await Plugin.StopAsync(default);await Provider.DisposeAsync();}finally{Environment.SetEnvironmentVariable("FGO_PET_STATE_ROOT",_oldRoot);Http.Dispose();}}
    }
    private sealed class Settings:IDialogueSettingsStore
    {public DialogueSettings Load()=>new(new("deepseek","https://api.deepseek.com/v1","deepseek-flash",contextWindowOverride:131072),true);public void Save(DialogueSettings s)=>throw new NotSupportedException();}
    private sealed class Authority(ConversationScope scope):IConversationScopeAuthority
    {public ConversationScope? Current=>scope;public bool IsActive(ToolScope s)=>s.RoleId==scope.ServantId&&s.ProjectId==scope.ProjectId;}
    private sealed class Access(ReviewBudgetUse use):IReviewBudgetAccess{public ReviewBudgetUse? Get(ConversationScope scope)=>use;}
    private sealed class Clock:TimeProvider
    {private long _ticks;public override DateTimeOffset GetUtcNow()=>DateTimeOffset.Parse("2026-10-20T00:00:00Z").AddTicks(_ticks);
        public override long GetTimestamp()=>_ticks;public override long TimestampFrequency=>TimeSpan.TicksPerSecond;public void Advance(int seconds)=>_ticks+=TimeSpan.FromSeconds(seconds).Ticks;}
    private sealed class Credentials:FgoPet.Infrastructure.Secrets.ICredentialReader
    {public int Reads;public Action? Before;public async Task<string?> ReadAsync(string target,CancellationToken token){await Task.Yield();Reads++;Before?.Invoke();return "synthetic-only-key";}}
    private sealed class SyntheticHttp:HttpMessageHandler
    {
        public int Sends;public bool WaitForCancellation;
        public readonly TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            Sends++;Entered.TrySetResult();if(WaitForCancellation)await Task.Delay(Timeout.Infinite,token);
            using var envelope=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            using var body=JsonDocument.Parse(envelope.RootElement.GetProperty("messages").EnumerateArray().Last().GetProperty("content").GetString()!);
            var data=body.RootElement;var binding=data.GetProperty("request").GetString();object result;
            if(data.TryGetProperty("proposals",out var candidates))result=new{schema=1,request=binding,verdicts=candidates.EnumerateArray().Select(p=>new{proposal=p.GetProperty("alias").GetString(),allow=true,reason="CURRENT_SELF"}).ToArray()};
            else result=new{schema=2,request=binding,proposals=new[]{new{domain="Self",kind="CurrentInterest",subject="assistant",content="Today I am curious about listening before agreement.",
                conditions="Synthetic project p at 2026-10-20; tentative current interest",sources=data.GetProperty("sources").EnumerateArray().Select(s=>s.GetProperty("alias").GetString()).ToArray(),target=(string?)null}}};
            var completion=JsonSerializer.Serialize(new{choices=new[]{new{delta=new{content=JsonSerializer.Serialize(result)},finish_reason="stop"}},usage=new{prompt_tokens=1000,completion_tokens=50}});
            return new(HttpStatusCode.OK){Content=new StringContent("data: "+completion+"\n\ndata: [DONE]\n\n",Encoding.UTF8,"text/event-stream")};
        }
    }
}
