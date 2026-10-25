using System.Runtime.CompilerServices;
using System.Text.Json;
using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Settings;
using FgoPet.Dialogue.Settings;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Providers;
using FgoPet.Understanding;
using FgoPet.Understanding.Contracts;
using Xunit;

namespace FgoPet.Plugin.Understanding.Tests;

public sealed class ReviewClassificationTests
{
    private readonly ConversationScope _scope=new("mash","p");
    [Theory][InlineData(true)][InlineData(false)]
    public async Task Independent_classification_is_bound_to_actual_material_and_charged_separately(bool allow)
    {
        var settings=new Settings();var ledger=new ReviewCallLedger(Path.Combine(Path.GetTempPath(),"fgo-classifier-"+Guid.NewGuid().ToString("N"),"ledger.db"));
        ledger.Approve(new("synthetic",ReviewBudgetedChatProvider.RouteHash(ModelRouteKey.From(settings.Load().ModelConnection!)),new('A',64),true));
        ledger.RegisterTrial(ReviewBatch.Formal,"R3A-1",_scope.Key,6);
        var budget=new Access(new(ledger,ReviewBatch.Formal,"R3A-1",_scope.Key));var resolver=new Resolver(allow);
        var factory=new ConfiguredReviewModelFactory(settings,resolver,new RequestTokenMeter(),budget);
        var root=new ConversationSourceSnapshot(new("c","mash","p"),"a",new('B',64),DateTimeOffset.Parse("2026-10-20T00:00:00Z"),ConversationSourceKind.Assistant,"Synthetic",1);
        var coverage=new UnderstandingSourceCoverage(root,0,4,4);
        var proposal=new UnderstandingProposal("proposal",UnderstandingDomain.Self,UnderstandingKind.CurrentInterest,"assistant","Current synthetic interest","This project, today only",[root]);
        var input=new ReviewModelInput("operation",_scope,[new(root,"text",coverage)],[],_=>{});
        var result=new ReviewModelResult([proposal],[coverage],new(100,10),new(100,TokenCountKind.Estimated,new('C',64)),new('D',64));
        var classification=await factory.ClassifyAsync(input,result,new(new(UnderstandingMode.Bounded,ReadSources:true,ExternalProcessing:true),1,1,0),new Gate(),default);
        Assert.NotNull(classification);Assert.Equal(allow,classification.IsLowImpact(proposal,[root]));
        Assert.False(classification.IsLowImpact(proposal with{Content="Changed into a user identity"},[root]));
        var call=Assert.Single(ledger.Snapshot().Calls);Assert.Equal("classify",call.Purpose);Assert.Equal(ReviewCallOutcome.Completed,call.Outcome);
        Assert.Equal(1,resolver.Sends);
    }
    private sealed class Settings:IDialogueSettingsStore
    {public DialogueSettings Load()=>new(new("deepseek","https://api.deepseek.com/v1","deepseek-flash"),true);public void Save(DialogueSettings s)=>throw new NotSupportedException();}
    private sealed class Access(ReviewBudgetUse use):IReviewBudgetAccess{public ReviewBudgetUse? Get(ConversationScope scope)=>use;}
    private sealed class Gate:IChatDispatchGate{public T Dispatch<T>(ChatRequest request,ModelRouteKey route,Func<T> start,CancellationToken token)=>start();}
    private sealed class Resolver(bool allow):IChatProviderResolver
    {
        public int Sends;public IChatProvider Resolve()=>throw new NotSupportedException();
        public IChatProvider Resolve(ModelConnectionSettings connection,IChatDispatchGate gate)=>new Provider(this,connection,gate,allow);
        private sealed class Provider(Resolver owner,ModelConnectionSettings connection,IChatDispatchGate gate,bool allowed):IChatProvider
        {
            public string ProviderId=>connection.ProviderId;public string ModelId=>connection.ModelId;
            public Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken token)=>throw new NotSupportedException();
            public async IAsyncEnumerable<ChatStreamChunk> StreamAsync(ChatRequest request,[EnumeratorCancellation]CancellationToken token)
            {
                Assert.Equal("understanding_review",request.Metadata["fgo_auxiliary"]);
                Assert.Contains("json",request.ModelMessages[0].Content,StringComparison.OrdinalIgnoreCase);
                using var data=JsonDocument.Parse(request.ModelMessages[^1].Content);
                var binding=data.RootElement.GetProperty("request").GetString();
                gate.Dispatch(request,ModelRouteKey.From(connection),()=>++owner.Sends,token);
                await Task.Yield();var json=JsonSerializer.Serialize(new{schema=1,request=binding,verdicts=new[]{new{proposal="p1",allow=allowed,reason=allowed?"CURRENT_SELF":"REVIEW_REQUIRED"}}});
                yield return new(json,FinishReason:"stop",Usage:new(500,20));yield return new("",IsComplete:true,FinishReason:"stop");
            }
        }
    }
}
