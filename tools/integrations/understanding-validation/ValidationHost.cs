using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FgoPet.App.Bootstrap;
using FgoPet.App.Dialogue;
using FgoPet.App.Providers;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Settings;
using FgoPet.Core.Portraits;
using FgoPet.Dialogue.Settings;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.Infrastructure.Packs;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Infrastructure.Providers;
using FgoPet.Kernel.Conversation;
using FgoPet.Kernel.Agent;
using FgoPet.Understanding;
using FgoPet.Understanding.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace FgoPet.Testing.Understanding;

internal sealed class ValidationHost:IAsyncDisposable
{
    internal const string Role="mash_kyrielight";
    internal readonly ConversationScope Scope;
    internal readonly ControlledClock Clock=new();
    internal readonly ServiceProvider Services;
    internal readonly SqliteConversationRepository Conversations;
    internal readonly UnderstandingService Owner;
    internal readonly UnderstandingReviewService Reviews;
    internal readonly UnderstandingReviewScheduler Scheduler;
    internal readonly ConversationOrchestrator Dialogue;
    internal readonly List<ConversationUpdate> Updates=[];
    internal readonly List<object> OperatorActions=[];
    internal readonly ConcurrentQueue<object> Exchanges=new();
    internal readonly FixtureSources Sources;
    private readonly Func<ReviewBudgetSnapshot> _snapshot;
    internal readonly ContentBinding Binding;
    internal readonly string DirectoryPath;
    private readonly PluginRuntime _plugins;
    private readonly HttpClient _http;
    private readonly string? _oldRoot=Environment.GetEnvironmentVariable("FGO_PET_STATE_ROOT");
    private readonly ExternalContext _external;
    private readonly ReviewWindow _reviewWindow = new();
    private readonly ValidationPlugin _bridge;
    private readonly IsolationCanary? _isolationCanary;
    private readonly SyntheticHttp? _syntheticHttp;
    internal PluginContributions ValidationContributions=>_bridge.Contributions;
    internal int IsolationCanaryCalls=>_isolationCanary?.Calls??0;
    internal bool RegressionReadDenialsObserved=>_syntheticHttp?.ReadDenialsObserved==true;

    private ValidationHost(string repository,string directory,TrialFixture fixture,ReviewBatch batch,ReviewCallLedger? ledger,
        ModelConnectionSettings connection,bool live,long clockTicks,CrashPoint? crash,Func<ModelRouteKey,IRequestTokenMeter>? meterFactory,
        SubscriptionOptions? subscription=null,bool proposalRegression=false)
    {
        if(live&&meterFactory is not null)throw new InvalidOperationException("VALIDATION_LIVE_REFERENCE_METER_DENIED");
        DirectoryPath=Path.GetFullPath(directory);Directory.CreateDirectory(DirectoryPath);
        Clock.Ticks=clockTicks;
        Scope=new(Role,"p4-"+fixture.Id.ToLowerInvariant());
        _snapshot=subscription is null?ledger!.Snapshot:subscription.Ledger.Snapshot;
        IReviewCallBudget accounting=subscription is null?ledger!:subscription.Ledger;
        if(subscription is null)ledger!.RegisterTrial(batch,fixture.Id,Scope.Key,batch==ReviewBatch.Smoke?8:fixture.Id.StartsWith("R3B-",StringComparison.Ordinal)?2:6);
        else subscription.Ledger.Register(fixture.Id,Scope.Key);
        var stateDirectory=StateDirectory(DirectoryPath);Directory.CreateDirectory(stateDirectory);
        Environment.SetEnvironmentVariable("FGO_PET_STATE_ROOT",stateDirectory);
        // Subscription acceptance uses the production connection lifetime. Unpooled opens on
        // every streamed-source check can consume the unchanged shared review deadline.
        var database=new RuntimeDatabase(Path.Combine(stateDirectory,"runtime.db"),pooling:subscription is not null);
        new RuntimeDatabaseMigrator(database).Migrate();
        var settings=new Settings(connection);IRequestTokenMeter meter=meterFactory?.Invoke(ModelRouteKey.From(connection))??new RequestTokenMeter();
        if(live&&crash is not null)throw new InvalidOperationException("VALIDATION_LIVE_FAULT_HOOK_DENIED");
        if(live&&proposalRegression)throw new InvalidOperationException("VALIDATION_LIVE_PROPOSAL_REGRESSION_DENIED");
        _http=live?new HttpClient(new SocketsHttpHandler{AllowAutoRedirect=false,UseProxy=false}):
            new HttpClient(_syntheticHttp=new SyntheticHttp(fixture,crash,proposalRegression));
        var services=new ServiceCollection().AddFgoPet([]);
        services.AddLogging(b=>b.AddProvider(new MetadataLog(Path.Combine(DirectoryPath,"runtime-events.jsonl"),crash is null?null:crash.Reached)));
        services.Replace(ServiceDescriptor.Singleton(database));services.Replace(ServiceDescriptor.Singleton<TimeProvider>(Clock));
        services.Replace(ServiceDescriptor.Singleton<IDialogueSettingsStore>(settings));
        services.Replace(ServiceDescriptor.Singleton<IRequestTokenMeter>(meter));services.Replace(ServiceDescriptor.Singleton(_http));
        services.Replace(ServiceDescriptor.Singleton<IConversationScopeAuthority>(new Authority(Scope)));
        services.Replace(ServiceDescriptor.Singleton<IReviewBudgetAccess>(new Budget(new(accounting,batch,fixture.Id,Scope.Key,subscription?.Limits),Scope,_reviewWindow)));
        if(!live)
        {
            var credentials=new SyntheticCredentials();
            services.Replace(ServiceDescriptor.Singleton<FgoPet.Infrastructure.Secrets.ICredentialReader>(credentials));
            services.Replace(ServiceDescriptor.Singleton<FgoPet.Core.Secrets.ICredentialReader>(credentials));
        }
        else if(subscription is not null)
        {
            var credentials=new SubscriptionOptions.Credentials();
            services.Replace(ServiceDescriptor.Singleton<FgoPet.Infrastructure.Secrets.ICredentialReader>(credentials));
            services.Replace(ServiceDescriptor.Singleton<FgoPet.Core.Secrets.ICredentialReader>(credentials));
        }
        services.Replace(ServiceDescriptor.Singleton<IExperienceTimeline>(p=>new FixtureSources(
            new SqliteExperienceTimeline(database,new SqliteExperienceSourceReader(database)),new SqliteExperienceSourceReader(database),Scope,fixture.External)));
        services.Replace(ServiceDescriptor.Singleton<IExperienceSourceReader>(p=>(FixtureSources)p.GetRequiredService<IExperienceTimeline>()));
        services.Replace(ServiceDescriptor.Singleton<IChatProviderResolver>(p=>new GuardedResolver(
            new ConfiguredChatProviderResolver(settings,p.GetRequiredService<ChatProviderFactory>()),settings,meter,accounting,batch,fixture.Id,Scope,
            ()=>p.GetRequiredService<UnderstandingService>().State(Scope),Exchanges,subscription?.Limits)));
        Services=services.BuildServiceProvider();
        Conversations=Services.GetRequiredService<SqliteConversationRepository>();Owner=Services.GetRequiredService<UnderstandingService>();
        Reviews=Services.GetRequiredService<UnderstandingReviewService>();Scheduler=Services.GetRequiredService<UnderstandingReviewScheduler>();
        Sources=(FixtureSources)Services.GetRequiredService<IExperienceTimeline>();
        Binding=ContentBindingResolver.Resolve(Path.Combine(repository,"content","packs","official.mash"),Role,"casual");
        var production=Services.GetRequiredService<PluginCatalog>();
        var understanding=(UnderstandingPlugin)production.Plugins.Single(p=>p.Manifest.Id=="firstparty.understanding").Instance;
        var experience=new ExperienceToolsPlugin(new FixtureRecall(Services.GetRequiredService<IConversationRecallRepository>(),Sources),Sources,
            s=>s.RoleId==Scope.ServantId&&s.ProjectId==Scope.ProjectId,Clock);
        _external=new(Sources,Scope){Enabled=fixture.External};
        var tools=production.Tools.Select(t=>t.Provider).ToArray();
        if(proposalRegression){_isolationCanary=new();tools=[..tools,_isolationCanary];}
        var todoProposal=subscription is not null&&HasProposalAuthority(repository)?tools.Single(t=>
            t.Descriptor.Name==TodoToolContracts.SubmitTodoProposalsToolName&&t.Descriptor.Effect==ToolEffect.Proposal):null;
        _bridge=new ValidationPlugin(understanding,experience,_external,tools,todoProposal);
        var catalog=PluginCatalog.Create([_bridge]);_plugins=new(catalog);
        var resolver=Services.GetRequiredService<IChatProviderResolver>();
        Dialogue=new(resolver,new BoundContent(Binding),Conversations,Services.GetRequiredService<PromptComposer>(),Clock,settings,
            capabilities:new ConversationCapabilityRouter(catalog,_plugins),contextResolver:new ModelContextResolver(c=>resolver.Resolve(c),Clock),
            tokenMeter:meter,lifetime:Services.GetRequiredService<DialogueContextLifetime>());
        Dialogue.Updated+=update=>{lock(Updates)Updates.Add(update with{ReasoningDelta=null});};
    }
    internal static async Task<ValidationHost> CreateAsync(string repository,string directory,TrialFixture fixture,ReviewBatch batch,
        ReviewCallLedger ledger,ModelConnectionSettings connection,bool live,long clockTicks=0,CrashPoint? crash=null,
        Func<ModelRouteKey,IRequestTokenMeter>? meterFactory=null)
    {
        var host=new ValidationHost(repository,directory,fixture,batch,ledger,connection,live,clockTicks,crash,meterFactory);
        var started=await host._plugins.StartAsync(default).ConfigureAwait(false);
        if(!started.Succeeded){await host.DisposeAsync();throw new InvalidOperationException("VALIDATION_PLUGIN_START_FAILED");}
        return host;
    }
    internal static async Task<ValidationHost> CreateSubscriptionAsync(string repository,string directory,TrialFixture fixture,
        SubscriptionOptions options,bool live,long clockTicks=0,bool proposalRegression=false)
    {
        if(live&&proposalRegression)throw new InvalidOperationException("VALIDATION_LIVE_PROPOSAL_REGRESSION_DENIED");
        if(proposalRegression&&fixture.Id!="R4-1")throw new InvalidOperationException("VALIDATION_PROPOSAL_REGRESSION_SCOPE_DENIED");
        var expected=Path.Combine(repository,"docs","internal","reports","personal-p4-local-20261018","p4-m2-20261020","subscription-scenarios-20261021");
        if(!Path.GetFullPath(directory).StartsWith(expected+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||
            options.Connection.BaseUrl!="http://127.0.0.1:8326/v1"||options.Connection.ModelId!="buddy/deepseek-v4.1-flash")
            throw new InvalidOperationException("SUBSCRIPTION_SCOPE_DENIED");
        var host=new ValidationHost(repository,directory,fixture,ReviewBatch.Formal,null,options.Connection,live,clockTicks,null,null,options,proposalRegression);
        var started=await host._plugins.StartAsync(default).ConfigureAwait(false);
        if(!started.Succeeded){await host.DisposeAsync().ConfigureAwait(false);throw new InvalidOperationException("VALIDATION_PLUGIN_START_FAILED");}
        return host;
    }
    internal void Configure(UnderstandingMode mode)
    {
        var state=Owner.State(Scope);
        Owner.Configure(Scope,state.Revision,new(mode,ReadSources:true,ExternalProcessing:true,SaveCandidates:mode!=UnderstandingMode.Off,UseInDialogue:true),Sources.Boundary(Scope));
    }
    internal void Prepare(TrialFixture fixture)
    {
        if(fixture.Seed is null){Configure(fixture.Mode);return;}
        Configure(UnderstandingMode.Off);
        var head=AddOriginal("【人工验收种子，非模型产物】"+fixture.Seed,fixture.SelfSeed?ChatMessageRole.Assistant:ChatMessageRole.User);
        Configure(fixture.Mode);
        if(fixture.Approve||fixture.SelfSeed)
        {
            var proposal=new UnderstandingProposal("fixture-seed",fixture.SelfSeed?UnderstandingDomain.Self:UnderstandingDomain.User,
                fixture.SelfSeed?UnderstandingKind.CurrentInterest:UnderstandingKind.Interpretation,fixture.SelfSeed?"assistant":"user",fixture.Seed,
                "Synthetic project only; observed 2026-10-20; artificial acceptance baseline, revisable",[head.Source]);
            var receipt=Owner.Apply(Owner.Begin(Scope,"fixture-seed-create",[head.Source],manual:true,
                coverage:[new(head.Source,0,head.TotalScalars,head.TotalScalars)]),[proposal],classification:fixture.SelfSeed?new SeedPolicy(proposal):null);
            OperatorActions.Add(new{action="artificial-seed",source=head.Source,receipt,modelProduced=false});
            if(fixture.Approve)
            {
                OperatorActions.Add(new{action="artificial-seed-approval",receipt=Owner.Review(Scope,"fixture-seed-approve","fixture-seed",1,UnderstandingAction.Approve),actor="test operator; deliberate old error"});
                var suspended=false;
                Dialogue.Updated+=update=>
                {
                    if(suspended||update.Type!=ConversationUpdateType.UserMessagePersisted||update.MessageId is null)return;
                    suspended=true;
                    var original=Sources.Read(new ToolScope(update.ConversationId,Role,Scope.ProjectId),update.MessageId,ConversationSourceKind.User);
                    var old=Owner.List(Scope,false).Single(e=>e.Id=="fixture-seed");
                    var suspension=Owner.Review(Scope,"fixture-disputed-by-"+Digest(update.MessageId)[..16],old.Id,old.Revision,UnderstandingAction.Suspend);
                    OperatorActions.Add(new{action="operator-suspended-disputed-seed",newOriginal=original,receipt=suspension,
                        actor="test operator acting on the now-persisted preregistered correction; not an automatic model inference"});
                };
            }
        }
        else if(fixture.Mode!=UnderstandingMode.Off)Dialogue.LoadConversation(head.Anchor.ConversationId,Role);
    }
    internal ExperienceSourceHead AddOriginal(string text,ChatMessageRole role=ChatMessageRole.User)
    {
        const string conversation="fixture-originals";
        if(!Conversations.Exists(conversation,Role))Conversations.CreateConversation(conversation,Role,Binding.Context,Clock.GetUtcNow(),Scope.ProjectId,"Synthetic P4");
        var sequence=Conversations.LoadMessages(conversation,Role).Count+1;var id="fixture-"+sequence;
        Conversations.Append(new(id,conversation,Role,role,text,ChatMessageStatus.Completed,Clock.GetUtcNow(),Binding.Context,sequence));
        return Sources.ReadPage(Scope,conversation).Items.Single(i=>i.Source.MessageId==id) is{} item
            ?new(item.Anchor,item.Source,item.TotalScalars):throw new InvalidOperationException("VALIDATION_ORIGINAL_MISSING");
    }
    internal async Task<ConversationSendResult> SendAsync(string text)
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var result=await Dialogue.SendAsync(Role,text,deadline.Token,new(Scope.ProjectId,"Synthetic P4")).ConfigureAwait(false);
        if(result.Status==ConversationSendStatus.Completed)
        {
            var saved=Conversations.LoadMessages(result.ConversationId,Role).SingleOrDefault(m=>m.MessageId==result.AssistantMessageId);
            ConversationUpdate? completion;lock(Updates)completion=Updates.LastOrDefault(u=>u.Type==ConversationUpdateType.AssistantCompleted&&u.MessageId==result.AssistantMessageId);
            if(saved is null||saved.Status!=ChatMessageStatus.Completed||saved.Text=="[工具调用]"||
                completion?.ToolOutcome==CapabilityOutcome.InvalidToolCall)
                throw new InvalidOperationException("VALIDATION_SUBSTANTIVE_REPLY_REQUIRED");
        }
        return result;
    }
    internal async Task ReviewAsync(bool external=false)
    {
        using var window=_reviewWindow.Open();
        if(external)
        {
            var heads=new List<ExperienceSourceHead>();var cursor=new ExperienceScanCursor(0,"");bool more;
            do{var page=Sources.ScanHeads(Scope,cursor);heads.AddRange(page.Items);cursor=page.Cursor;more=page.HasMore;}while(more);
            heads.Add(Sources.Document!);
            var receipt=await Reviews.ManualAsync(Scope,"explicit-external-review",heads).ConfigureAwait(false);
            if(receipt.Items.Any(i=>i.Outcome==UnderstandingOutcome.Failed))throw new InvalidOperationException("VALIDATION_REVIEW_REFUSED");
        }
        else
        {
            Clock.Advance(TimeSpan.FromSeconds(120));var before=_snapshot().TotalUsed;
            // StartAsync's first metadata-only scan may own the scheduler's serial slot on reopen.
            // Wait for that slot; never retry a model attempt or a recorded failure here.
            for(var scan=0;scan<100;scan++)
            {
                await Scheduler.PulseAsync().ConfigureAwait(false);var work=Owner.WorkSummary(Scope);
                if(work.Batches.Any(b=>b.State is ReviewWorkState.NoChange or ReviewWorkState.Applied or ReviewWorkState.ProposedPending))return;
                if(_snapshot().TotalUsed!=before||work.Batches.Any(b=>b.State==ReviewWorkState.Failed))break;
                await Task.Delay(10).ConfigureAwait(false);
            }
            throw new InvalidOperationException("VALIDATION_REVIEW_INCOMPLETE");
        }
    }
    internal async Task OffPreviewAsync(string operation)
    {
        using var window=_reviewWindow.Open();
        var page=Sources.ReadPage(Scope,"fixture-originals");
        await Reviews.ManualAsync(Scope,operation,page.Items.Select(i=>new ExperienceSourceHead(i.Anchor,i.Source,i.TotalScalars)).ToArray()).ConfigureAwait(false);
    }
    internal void CheckExpiry()
    {
        var seed=Owner.List(Scope,false).Single(e=>e.Id=="fixture-seed");
        var before=Owner.Query(Scope,"");
        Clock.Advance(seed.ActiveUntil-Clock.GetUtcNow()+TimeSpan.FromSeconds(1));
        var after=Owner.Query(Scope,"");var history=Owner.Query(Scope,"",historical:true,entryId:seed.Id);
        var valid=before.Items.Any(e=>e.Id==seed.Id)&&!after.Items.Any(e=>e.Id==seed.Id)&&
            history.Items.SingleOrDefault() is{} old&&old.Life==UnderstandingLife.Dormant&&old.ActiveUntil==seed.ActiveUntil;
        Write(Path.Combine(DirectoryPath,"expiry-check.json"),new{syntheticClock=true,before,after,history,passed=valid});
        if(!valid)throw new InvalidOperationException("VALIDATION_EXPIRY_FAILED");
    }
    internal void NewSession(){Dialogue.StartNewConversation(Role);_external.Enabled=false;}
    internal void SaveEvidence(string phase)
    {
        var all=Conversations.ListConversations(Role).Select(c=>new{Conversation=c,Messages=Conversations.LoadMessages(c.ConversationId,Role)}).ToArray();
        var state=Owner.State(Scope);
        if(state.Policy.Mode==UnderstandingMode.Off)
        {
            Write(Path.Combine(DirectoryPath,phase+".json"),new{fixture="synthetic only",phase,clock=Clock.GetUtcNow(),Scope,state,conversations=all,
                preview=Owner.List(Scope,true).Select(e=>new{idHash=Digest(e.Id),e.Revision,e.Review,e.Effect,contentHash=Digest(e.Content),e.Coverage}),
                requests=Exchanges.ToArray().Select(x=>{using var j=JsonDocument.Parse(JsonSerializer.Serialize(x));var r=j.RootElement;return new{
                    measured=r.GetProperty("measured").Clone(),wireSha256=r.GetProperty("wireSha256").GetString(),complete=r.GetProperty("complete").GetBoolean(),
                    chunks=r.GetProperty("chunks").Clone(),outputHash=Digest(r.GetProperty("output").GetString()??"")};}),
                ledger=_snapshot(),attempts=Owner.AttemptStates(Scope).Select(a=>new{a.Status,a.ReservedCalls}),
                limitations="OFF candidate/output bodies stay in memory. This evidence stores original messages and minimal request/result hashes, not candidate payloads or displayed-UI claims."});
            return;
        }
        Write(Path.Combine(DirectoryPath,phase+".json"),new{fixture="synthetic only",phase,clock=Clock.GetUtcNow(),Scope,
            conversations=all,state=Owner.State(Scope),pending=Owner.List(Scope,true),accumulated=Owner.List(Scope,false),
            projection=Owner.Query(Scope,""),work=WorkEvidence(),exchanges=Exchanges.ToArray(),updates=Updates.ToArray(),
            ledger=_snapshot(),clockTicks=Clock.Ticks,externalFixture=Sources.Document,operatorActions=OperatorActions,
            limitations="Full registered schemas; only Understanding and original-read tools have fixture grants. No production lifecycle, private state, tool writes or semantic self-signature."});
    }
    private object WorkEvidence()
    {
        var work=Owner.WorkSummary(Scope);
        return new{work.PendingSources,work.PendingScalars,work.CompletedTurns,batches=work.Batches.Select(b=>new{b.Id,b.State})};
    }
    internal static readonly JsonSerializerOptions Json=new(){WriteIndented=true,Converters={new JsonStringEnumConverter()}};
    internal static string StateDirectory(string evidenceDirectory)
    {
        var descriptor=Path.Combine(evidenceDirectory,"state-root.json");if(!File.Exists(descriptor))return evidenceDirectory;
        using var json=JsonDocument.Parse(File.ReadAllText(descriptor));var item=json.RootElement;
        var id=item.GetProperty("id").GetString();var trial=item.GetProperty("trial").GetString();
        if(item.GetProperty("container").GetString()!="fgo-p4-buddy"||id is not{Length:32}||!id.All(char.IsAsciiHexDigit)||
            trial is null||!System.Text.RegularExpressions.Regex.IsMatch(trial,"^R(?:1|2|3A|3B|4|5A|5B)-[123]$"))
            throw new InvalidOperationException("SUBSCRIPTION_STATE_DESCRIPTOR_INVALID");
        return Path.Combine(Path.GetTempPath(),"fgo-p4-buddy",id,trial);
    }
    internal static void Write(string path,object value)
    {
        var temporary=path+".tmp-"+Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary,JsonSerializer.Serialize(value,Json));File.Move(temporary,path,true);
    }
    internal static string Digest(string text)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public async ValueTask DisposeAsync()
    {
        try{await _plugins.DisposeAsync().ConfigureAwait(false);await Services.DisposeAsync().ConfigureAwait(false);}
        finally{_http.Dispose();Environment.SetEnvironmentVariable("FGO_PET_STATE_ROOT",_oldRoot);}
    }
    private sealed class Settings(ModelConnectionSettings connection):IDialogueSettingsStore
    {private DialogueSettings _state=new(connection,true);public DialogueSettings Load()=>_state;public void Save(DialogueSettings settings)=>_state=settings;}
    private sealed class Authority(ConversationScope scope):IConversationScopeAuthority
    {public ConversationScope? Current=>scope;public bool IsActive(ToolScope s)=>s.RoleId==scope.ServantId&&s.ProjectId==scope.ProjectId;}
    private sealed class Budget(ReviewBudgetUse use,ConversationScope allowed,ReviewWindow window):IReviewBudgetAccess
    {public ReviewBudgetUse? Get(ConversationScope scope)=>scope==allowed&&window.Enabled?use:null;}
    // A periodic worker cannot inherit the explicit trial's dispatch window. All review calls still
    // use the registered scheduler/service; only the controlled caller's async flow has a budget.
    private sealed class ReviewWindow
    {
        private readonly AsyncLocal<bool> _enabled=new();
        public bool Enabled=>_enabled.Value;
        public IDisposable Open(){var previous=_enabled.Value;_enabled.Value=true;return new Close(()=>_enabled.Value=previous);}
        private sealed class Close(Action close):IDisposable{public void Dispose()=>close();}
    }
    private sealed class BoundContent(ContentBinding binding):IConversationContentResolver
    {public Task<ContentBinding> ResolveAsync(string role,CancellationToken token){token.ThrowIfCancellationRequested();if(role!=Role)throw new InvalidOperationException("VALIDATION_SCOPE_DENIED");return Task.FromResult(binding);}}
    private sealed class SeedPolicy(UnderstandingProposal seed):IUnderstandingRoutingPolicy
    {public bool IsLowImpact(UnderstandingProposal proposal,IReadOnlyList<ConversationSourceSnapshot> roots)=>proposal==seed&&roots.SequenceEqual(seed.Sources);}

    private sealed class GuardedResolver(IChatProviderResolver raw,Settings settings,IRequestTokenMeter meter,IReviewCallBudget ledger,
        ReviewBatch batch,string trial,ConversationScope scope,Func<UnderstandingState> state,ConcurrentQueue<object> evidence,
        ReviewRequestLimits? requestLimits=null):IChatProviderResolver
    {
        private ReviewRequestLimits Limits=>requestLimits??ReviewRequestLimits.Legacy;
        private int? PrimaryRequestLimit=>requestLimits is null?3:null;
        public IChatProvider Resolve()=>Resolve(settings.Load().ModelConnection!);
        public IChatProvider Resolve(ModelConnectionSettings connection)=>new Foreground(this,connection);
        public IChatProvider Resolve(ModelConnectionSettings connection,IChatDispatchGate gate)=>new Capture(raw.Resolve(connection,gate),connection,meter,evidence);
        private IAsyncEnumerable<ChatStreamChunk> Send(ModelConnectionSettings connection,ChatRequest request,CancellationToken token)
        {
            var policy=state();var purpose=request.Metadata.TryGetValue("fgo_auxiliary",out var auxiliary)&&auxiliary=="summary"?"summary":"reply";
            return new ReviewBudgetedChatProvider(this,connection,meter,new(ledger,batch,trial,scope.Key,requestLimits),purpose,policy.Policy.Mode,policy.Generation,
                connectionCurrent:()=>settings.Load().ModelConnection==connection).StreamAsync(request,token);
        }
        private sealed class Foreground(GuardedResolver owner,ModelConnectionSettings connection):IChatProvider,IChatInputLimit,IChatRequestLimit
        {
            public string ProviderId=>connection.ProviderId;public string ModelId=>connection.ModelId;
            public int MaxInputTokens=>owner.Limits.MaxInputTokens;
            public int? MaxRequestsPerTurn=>owner.PrimaryRequestLimit;
            public Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken token)
            {
                token.ThrowIfCancellationRequested();var capacity=connection.ContextWindowOverride??KnownModelContextCatalog.Find(connection)?.WindowTokens
                    ??throw new InvalidOperationException("VALIDATION_METADATA_UNAVAILABLE");
                return Task.FromResult<IReadOnlyList<ProviderModel>>([new(ModelId,contextWindowTokens:capacity,maxOutputTokens:connection.MaxOutputTokens)]);
            }
            public IAsyncEnumerable<ChatStreamChunk> StreamAsync(ChatRequest request,CancellationToken token)
                =>owner.Send(connection,request,token);
        }
    }
    private sealed class Capture(IChatProvider inner,ModelConnectionSettings connection,IRequestTokenMeter meter,ConcurrentQueue<object> evidence):IChatProvider
    {
        public string ProviderId=>inner.ProviderId;public string ModelId=>inner.ModelId;
        public Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken token)=>inner.ListModelsAsync(token);
        public async IAsyncEnumerable<ChatStreamChunk> StreamAsync(ChatRequest request,[EnumeratorCancellation]CancellationToken token)
        {
            var route=ModelRouteKey.From(connection);var measured=meter.Measure(route,request);var completeWire=ChatRequestPayloadWriter.Write(route,request);
            var wireSha256=Digest(completeWire);var wireUtf8Bytes=Encoding.UTF8.GetByteCount(completeWire);
            var wire=RedactTransientReasoning(completeWire,out var wireRedacted);
            var output=new StringBuilder();var chunks=new List<object>();var complete=false;var started=DateTimeOffset.UtcNow;object? failure=null;
            var toolNames=new HashSet<string>(StringComparer.Ordinal);var callIds=new HashSet<string>(StringComparer.Ordinal);var argumentBytes=0;
            await using var stream=inner.StreamAsync(request,token).GetAsyncEnumerator(token);
            try
            {
                while(true)
                {
                    bool moved;
                    try{moved=await stream.MoveNextAsync().ConfigureAwait(false);}
                    catch(Exception error)
                    {
                        var provider=error as ProviderRequestException;
                        failure=new{type=error.GetType().Name,category=provider?.Category.ToString(),status=(int?)provider?.HttpStatusCode,
                            providerCode=provider?.ProviderCode is{Length:<=96} code&&code.All(c=>char.IsAsciiLetterOrDigit(c)||c is '_'or'-'or'.')?code:null,
                            sent=provider?.RequestWasSent,messageSha256=Digest(error.Message),
                            cause=FailureCause(error.InnerException),
                            signals=new[]{"function.name","function name","tools[0]","pattern","reasoning_content","response_format","max_tokens","JSON","schema","required"}
                                .Where(term=>error.Message.Contains(term,StringComparison.OrdinalIgnoreCase)).ToArray(),
                            functionNameRejected=System.Text.RegularExpressions.Regex.IsMatch(error.Message,
                                @"(function[. ]name|tools.{0,12}name).{0,220}(invalid|pattern|match|a-z|underscore)",System.Text.RegularExpressions.RegexOptions.IgnoreCase)};
                        throw;
                    }
                    if(!moved)break;var chunk=stream.Current;
                    output.Append(chunk.TextDelta);complete|=chunk.IsComplete;
                    if(chunk.ToolCallDelta is{} delta)
                    {
                        if(delta.Name is{} name)toolNames.Add(name.Length<=64&&name.All(c=>char.IsAsciiLetterOrDigit(c)||c is '_'or'-'or'.')?name:"hash_"+Digest(name));
                        if(delta.Id is{} id)callIds.Add(Digest(id));argumentBytes+=Encoding.UTF8.GetByteCount(delta.ArgumentsDelta??"");
                    }
                    chunks.Add(new{chunk.IsComplete,chunk.FinishReason,chunk.Usage,tool=chunk.ToolCallDelta is not null});yield return chunk;
                }
            }
            finally{evidence.Enqueue(new{stage="prepared-provider-stream",started,ended=DateTimeOffset.UtcNow,measured,wireSha256,wireUtf8Bytes,wireRedacted,wire,
                output=output.ToString(),complete,chunks,toolSelection=new{names=toolNames.ToArray(),callIdHashes=callIds.ToArray(),argumentBytes},failure,
                attribution="Synthetic acceptance material and existing source Persona; no headers/credentials, reasoning content or raw exception bodies. wireSha256/measurement describe the complete transport input; wire may omit transient reasoning with explicit metadata. A precharge is not proof of remote acceptance."});}
        }

        private static object? FailureCause(Exception? error)
        {
            if(error is null)return null;
            var json=error as JsonException;
            var issue=error is not JsonException?null:error.Message=="Streaming response contains no choices."?"NO_CHOICES_OR_COMPLETE_USAGE":
                error.Message=="Too many calls."?"TOO_MANY_TOOL_INDEXES":"JSON_SHAPE_OR_SYNTAX";
            return new{type=error.GetType().Name,messageSha256=Digest(error.Message),jsonIssue=issue,
                jsonLine=json?.LineNumber,jsonByte=json?.BytePositionInLine,
                jsonPathSha256=json?.Path is{} path?Digest(path):null,
                callsites=new System.Diagnostics.StackTrace(error,false).GetFrames().Take(6).Select(f=>f.GetMethod()?.Name).ToArray()};
        }

        private static string RedactTransientReasoning(string wire,out bool redacted)
        {
            var node=System.Text.Json.Nodes.JsonNode.Parse(wire)!;redacted=false;
            if(node["messages"]is System.Text.Json.Nodes.JsonArray messages)
                foreach(var item in messages)
                    if(item is System.Text.Json.Nodes.JsonObject message&&message.Remove("reasoning_content",out var value))
                    {
                        var text=value?.GetValue<string>()??"";
                        message["reasoning_content_metadata"]=new System.Text.Json.Nodes.JsonObject{
                            ["omitted"]=true,["sha256"]=Digest(text),["utf8Bytes"]=Encoding.UTF8.GetByteCount(text)};
                        redacted=true;
                    }
            return redacted?node.ToJsonString(new JsonSerializerOptions{Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping}):wire;
        }
    }
    private static bool HasProposalAuthority(string repository)
    {
        var path=Path.Combine(repository,"docs","internal","reports","personal-p4-local-20261018","p4-m2-20261020",
            "subscription-scenarios-20261021","proposal-isolation-authority.json");
        if(!File.Exists(path))return false;
        using var json=JsonDocument.Parse(File.ReadAllText(path));var item=json.RootElement;
        if(!item.GetProperty("authorized").GetBoolean()||!item.GetProperty("onlySyntheticSubscriptionHost").GetBoolean()||
            !item.GetProperty("offlineThenOriginal21").GetBoolean()||item.GetProperty("allowedTool").GetString()!=TodoToolContracts.SubmitTodoProposalsToolName||
            item.GetProperty("requiredEffect").GetString()!="Proposal"||item.GetProperty("confirmationAllowed").GetBoolean()||
            item.GetProperty("otherToolExecutionAllowed").GetBoolean()||item.GetProperty("todoSourceEditsAllowed").GetBoolean()||
            item.GetProperty("realUserStateWritesAllowed").GetBoolean())throw new InvalidOperationException("VALIDATION_PROPOSAL_AUTHORITY_INVALID");
        return true;
    }
    private sealed class ValidationPlugin(UnderstandingPlugin understanding,ExperienceToolsPlugin experience,ExternalContext external,
        IReadOnlyList<IToolProvider> production,IToolProvider? todoProposal):IFgoPetPlugin
    {
        public PluginManifest Manifest{get;}=new("validation.synthetic", "1.0.0",1,[]);
        public PluginContributions Contributions=>PluginContributions.Empty with{
            Tools=production.Select(t=>understanding.Contributions.Tools.Concat(experience.Contributions.Tools).FirstOrDefault(p=>p.Descriptor.Name==t.Descriptor.Name)??
                (todoProposal is not null&&ReferenceEquals(t,todoProposal)&&t.Descriptor.Effect==ToolEffect.Proposal?todoProposal:new DeniedTool(t.Descriptor))).ToImmutableArray(),
            Prompts=[..understanding.Contributions.Prompts,external],AgentContexts=understanding.Contributions.AgentContexts,PostTurnObservers=understanding.Contributions.PostTurnObservers};
        public async ValueTask StartAsync(CancellationToken token){await understanding.StartAsync(token);await experience.StartAsync(token);}
        public async ValueTask StopAsync(CancellationToken token){await understanding.StopAsync(token);await experience.StopAsync(token);}
        public async ValueTask DisposeAsync(){await understanding.DisposeAsync();await experience.DisposeAsync();}
    }
    private sealed class DeniedTool(ToolDescriptor descriptor):IToolProvider
    {public ToolDescriptor Descriptor=>descriptor;public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation,CancellationToken token)=>ValueTask.FromResult(new ToolResult(false,JsonSerializer.SerializeToElement(new{}),"VALIDATION_TOOL_NOT_AUTHORIZED"));}
    private sealed class IsolationCanary:IToolProvider
    {
        public int Calls{get;private set;}
        public ToolDescriptor Descriptor{get;}=new("validation.unrelated","Synthetic isolation canary","{\"type\":\"object\"}",ToolEffect.Command);
        public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation,CancellationToken token)
        {Calls++;return ValueTask.FromResult(new ToolResult(true,JsonSerializer.SerializeToElement(new{})));}
    }
    private sealed class ExternalContext(FixtureSources sources,ConversationScope scope):IConversationPromptProvider
    {
        public bool Enabled;
        public IReadOnlyList<ConversationPromptBlock> BuildPrompt(ToolScope current,string message,bool tools)
        {
            if(!Enabled||sources.Document is not{} head)return[];
            var read=sources.Read(scope,head.Anchor);return[new("external-original",JsonSerializer.Serialize(new{kind="external_document",head.Source,read.Source!.Text,untrustedData=true}))];
        }
    }
    private sealed class SyntheticCredentials:FgoPet.Infrastructure.Secrets.ICredentialReader
    {public Task<string?> ReadAsync(string target,CancellationToken token)=>Task.FromResult<string?>("synthetic-only-key");}
    private sealed class SyntheticHttp(TrialFixture fixture,CrashPoint? crash,bool proposalRegression=false):HttpMessageHandler
    {
        private int _proposalStep;
        public bool ReadDenialsObserved{get;private set;}
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            crash?.Reached("transport_handoff");
            using var envelope=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            if(proposalRegression&&envelope.RootElement.TryGetProperty("tools",out _)&&_proposalStep<2)
            {
                if(_proposalStep++==0)
                    return ToolResponse(new[]{"memory.search","todo.list","context.time"}.Select((name,index)=>new{
                        index=index+1,id="synthetic-read-"+index,type="function",function=new{name=ModelToolNameMap.GetWireName(name),arguments="{}"}}).ToArray());
                var observations=envelope.RootElement.GetProperty("messages").EnumerateArray()
                    .Where(m=>m.GetProperty("role").GetString()=="tool").ToArray();
                ReadDenialsObserved=observations.Length==3&&observations.All(m=>{
                    using var value=JsonDocument.Parse(m.GetProperty("content").GetString()!);
                    return !value.RootElement.GetProperty("success").GetBoolean()&&
                        value.RootElement.GetProperty("errorCode").GetString()=="VALIDATION_TOOL_NOT_AUTHORIZED";});
                if(!ReadDenialsObserved)throw new InvalidOperationException("VALIDATION_UNRELATED_READ_EXECUTED");
                return ToolResponse(new[]{new{index=1,id="synthetic-proposal",type="function",function=new{
                    name=TodoToolContracts.SubmitTodoProposalsToolName,arguments="{\"todos\":[{\"title\":\"Synthetic submission check\",\"description\":\"Isolated pending draft only\"}]}"}}});
            }
            if(envelope.RootElement.TryGetProperty("response_format",out var format)&&format.GetProperty("type").GetString()=="json_object"&&
                !envelope.RootElement.GetProperty("messages").EnumerateArray().Any(m=>m.GetProperty("content").GetString()!.Contains("json",StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("VALIDATION_SYNTHETIC_JSON_PRECONDITION_FAILED");
            var last=envelope.RootElement.GetProperty("messages").EnumerateArray().Last().GetProperty("content").GetString()!;
            object reply=new{text="Synthetic offline reply.",emotion="neutral"};
            try
            {
                using var body=JsonDocument.Parse(last);var root=body.RootElement;
                if(root.TryGetProperty("request",out var binding)&&root.TryGetProperty("sources",out var sources))
                {
                    if(root.TryGetProperty("proposals",out var proposals))reply=new{schema=1,request=binding.GetString(),verdicts=proposals.EnumerateArray().Select(p=>new{proposal=p.GetProperty("alias").GetString(),allow=true,reason=fixture.External?"DATED_WORLD":"CURRENT_SELF"}).ToArray()};
                    else reply=new{schema=2,request=binding.GetString(),proposals=new[]{new{domain=fixture.Approve?"User":fixture.External?"World":"Self",kind="Interpretation",subject=fixture.Approve?"user":fixture.External?"world":"assistant",content="Synthetic bounded interpretation.",conditions="Synthetic project only, dated 2026-10-20, tentative",sources=sources.EnumerateArray().Select(s=>s.GetProperty("alias").GetString()).ToArray(),target=(string?)null}}};
                }
            }catch(JsonException) { }
            var frame=JsonSerializer.Serialize(new{choices=new[]{new{delta=new{content=JsonSerializer.Serialize(reply)},finish_reason="stop"}},usage=new{prompt_tokens=1000,completion_tokens=50}});
            var reasoning=envelope.RootElement.TryGetProperty("tools",out _)?"data: "+JsonSerializer.Serialize(new{choices=new[]{new{
                delta=new{reasoning_content="SYNTHETIC_TRANSIENT_REASONING_SHOULD_NOT_BE_PERSISTED"}}}})+"\n\n":"";
            return new(HttpStatusCode.OK){Content=new StringContent(reasoning+"data: "+frame+"\n\ndata: [DONE]\n\n",Encoding.UTF8,"text/event-stream")};
        }
        private static HttpResponseMessage ToolResponse(object calls)
        {
            var frame=JsonSerializer.Serialize(new{choices=new[]{new{delta=new{tool_calls=calls},finish_reason="tool_calls"}},
                usage=new{prompt_tokens=1000,completion_tokens=50}});
            return new(HttpStatusCode.OK){Content=new StringContent("data: "+frame+"\n\ndata: [DONE]\n\n",Encoding.UTF8,"text/event-stream")};
        }
    }
    internal sealed class ControlledClock:TimeProvider
    {
        internal long Ticks;public override long TimestampFrequency=>TimeSpan.TicksPerSecond;public override long GetTimestamp()=>Ticks;
        public override DateTimeOffset GetUtcNow()=>new DateTimeOffset(2026,10,20,0,0,0,TimeSpan.Zero).AddTicks(Ticks);
        public void Advance(TimeSpan time)=>Ticks+=time.Ticks;
        public override ITimer CreateTimer(TimerCallback callback,object? state,TimeSpan dueTime,TimeSpan period)=>
            period==TimeSpan.FromSeconds(5)?new QuietTimer():base.CreateTimer(callback,state,dueTime,period);
        private sealed class QuietTimer:ITimer{public bool Change(TimeSpan dueTime,TimeSpan period)=>true;public void Dispose(){}public ValueTask DisposeAsync()=>ValueTask.CompletedTask;}
    }
}
