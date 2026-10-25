using System.Runtime.CompilerServices;
using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Settings;
using FgoPet.Infrastructure.Providers;
using FgoPet.Understanding;
using FgoPet.Understanding.Contracts;
using Xunit;

namespace FgoPet.Plugin.Understanding.Tests;

public sealed class ReviewBudgetedProviderTests
{
    private static readonly ModelConnectionSettings Connection=new("deepseek","https://api.deepseek.com/v1","deepseek-flash",maxOutputTokens:2048);
    private static ChatRequest Request()=>new("synthetic","conversation",[new(ChatMessageRole.User,"Synthetic only")],maxOutputTokens:2048);
    private static ReviewCallLedger Ledger()
    {
        var ledger=new ReviewCallLedger(Path.Combine(Path.GetTempPath(),"fgo-metered-provider-"+Guid.NewGuid().ToString("N"),"ledger.db"));
        ledger.Approve(new("synthetic-task",ReviewBudgetedChatProvider.RouteHash(ModelRouteKey.From(Connection)),new('A',64),true));
        ledger.RegisterTrial(ReviewBatch.Formal,"R1-1","synthetic",1);return ledger;
    }

    [Fact]
    public async Task Explicit_host_limits_allow_complete_input_and_output_above_legacy_caps()
    {
        var budget=new AuthorizedBudget();var resolver=new Resolver(()=>Assert.Single(budget.Plans));
        var provider=new ReviewBudgetedChatProvider(resolver,Connection,new RequestTokenMeter(),
            new(budget,ReviewBatch.Formal,"subscription-scenario","synthetic",new(128000,8192)),
            "reply",UnderstandingMode.Proposals,1);
        var request=new ChatRequest("synthetic","c",[new(ChatMessageRole.User,new string('中',6000))],maxOutputTokens:8192);
        await foreach(var chunk in provider.StreamAsync(request,default)) { }
        Assert.True(Assert.Single(budget.Plans).EstimatedInputTokens>16000);
        Assert.Equal(128000,provider.MaxInputTokens);Assert.Equal(1,resolver.Sends);
    }

    [Theory][InlineData(1000,8192)][InlineData(128000,4096)]
    public async Task Explicit_actual_model_capacity_still_refuses_before_reservation(int input,int output)
    {
        var budget=new AuthorizedBudget();var resolver=new Resolver(()=>{});
        var provider=new ReviewBudgetedChatProvider(resolver,Connection,new RequestTokenMeter(),
            new(budget,ReviewBatch.Formal,"subscription-scenario","synthetic",new(input,output)),
            "reply",UnderstandingMode.Proposals,1);
        var request=new ChatRequest("synthetic","c",[new(ChatMessageRole.User,new string('中',6000))],maxOutputTokens:8192);
        await Assert.ThrowsAsync<InvalidOperationException>(async()=>{await foreach(var chunk in provider.StreamAsync(request,default)) { }});
        Assert.Empty(budget.Plans);Assert.Equal(0,resolver.Sends);
    }

    [Fact]
    public async Task Reservation_is_durable_before_transport_and_terminal_usage_is_accounted()
    {
        var ledger=Ledger();var resolver=new Resolver(()=>Assert.Equal(1,ledger.Snapshot().TotalUsed));
        var provider=new ReviewBudgetedChatProvider(resolver,Connection,new RequestTokenMeter(),
            new(ledger,ReviewBatch.Formal,"R1-1","synthetic"),"review",UnderstandingMode.Proposals,4);
        await foreach(var chunk in provider.StreamAsync(Request(),default)) { }
        var row=Assert.Single(ledger.Snapshot().Calls);
        Assert.Equal(ReviewCallOutcome.Completed,row.Outcome);
        Assert.Equal(123,row.InputTokens);Assert.Equal(2,row.OutputTokens);Assert.Equal(1,resolver.Sends);
    }

    [Fact]
    public async Task An_exhausted_trial_never_reaches_the_transport()
    {
        var ledger=Ledger();var resolver=new Resolver(()=>{});
        var provider=new ReviewBudgetedChatProvider(resolver,Connection,new RequestTokenMeter(),
            new(ledger,ReviewBatch.Formal,"R1-1","synthetic"),"review",UnderstandingMode.Proposals,4);
        var request=Request();
        Assert.NotNull(ledger.TryReserve(new(ReviewBatch.Formal,"R1-1","synthetic","previous",
            ReviewBudgetedChatProvider.RouteHash(ModelRouteKey.From(Connection)),"review",UnderstandingMode.Proposals,4,new('C',64),1000)));
        var error=await Assert.ThrowsAsync<InvalidOperationException>(async()=>{await foreach(var chunk in provider.StreamAsync(request,default)) { }});
        Assert.Equal("UNDERSTANDING_BUDGET_LIMITED",error.Message);Assert.Equal(0,resolver.Sends);
    }
    [Theory][InlineData(4096,false)][InlineData(2048,true)]
    public async Task Complete_input_and_output_limits_refuse_before_precharge(int output,bool oversized)
    {
        var ledger=Ledger();var resolver=new Resolver(()=>{});var provider=new ReviewBudgetedChatProvider(resolver,Connection,new RequestTokenMeter(),
            new(ledger,ReviewBatch.Formal,"R1-1","synthetic"),"reply",UnderstandingMode.Proposals,1);
        var request=new ChatRequest("synthetic","c",[new(ChatMessageRole.User,oversized?new string('中',6000):"Synthetic")],maxOutputTokens:output);
        await Assert.ThrowsAsync<InvalidOperationException>(async()=>{await foreach(var chunk in provider.StreamAsync(request,default)) { }});
        Assert.Equal(0,resolver.Sends);Assert.Equal(0,ledger.Snapshot().TotalUsed);
    }
    [Fact] public async Task A_changed_connection_and_a_revoked_owner_admission_never_charge_or_send()
    {
        foreach(var routeChanged in new[]{true,false})
        {
            var ledger=Ledger();var resolver=new Resolver(()=>{});
            var provider=new ReviewBudgetedChatProvider(resolver,Connection,new RequestTokenMeter(),new(ledger,ReviewBatch.Formal,"R1-1","synthetic"),
                "review",UnderstandingMode.Bounded,1,new DeniedGate(),()=>!routeChanged);
            await Assert.ThrowsAsync<InvalidOperationException>(async()=>{await foreach(var chunk in provider.StreamAsync(Request(),default)) { }});
            Assert.Equal(0,resolver.Sends);Assert.Equal(0,ledger.Snapshot().TotalUsed);
        }
    }
    [Fact] public async Task Incomplete_response_keeps_its_charge_and_a_second_send_is_denied()
    {
        var ledger=Ledger();var resolver=new Resolver(()=>{}){Incomplete=true};
        var provider=new ReviewBudgetedChatProvider(resolver,Connection,new RequestTokenMeter(),new(ledger,ReviewBatch.Formal,"R1-1","synthetic"),"review",UnderstandingMode.Proposals,1);
        await foreach(var chunk in provider.StreamAsync(Request(),default)) { }
        Assert.Equal(ReviewCallOutcome.Unknown,Assert.Single(ledger.Snapshot().Calls).Outcome);
        await Assert.ThrowsAsync<InvalidOperationException>(async()=>{await foreach(var chunk in provider.StreamAsync(Request(),default)) { }});
        Assert.Equal(1,resolver.Sends);
    }
    [Fact] public async Task A_connection_change_inside_metering_is_rechecked_at_the_handoff()
    {
        var changed=false;var ledger=Ledger();var resolver=new Resolver(()=>{});
        var provider=new ReviewBudgetedChatProvider(resolver,Connection,new SwitchingMeter(()=>changed=true),
            new(ledger,ReviewBatch.Formal,"R1-1","synthetic"),"review",UnderstandingMode.Proposals,1,connectionCurrent:()=>!changed);
        await Assert.ThrowsAsync<InvalidOperationException>(async()=>{await foreach(var chunk in provider.StreamAsync(Request(),default)) { }});
        Assert.Equal(0,resolver.Sends);Assert.Equal(0,ledger.Snapshot().TotalUsed);
    }
    private sealed class AuthorizedBudget:IReviewCallBudget
    {
        public List<ReviewCallPlan> Plans {get;}=[];
        public ReviewCallReservation? TryReserve(ReviewCallPlan plan)
        {Plans.Add(plan);return new(new('D',64),1,Plans.Count,Plans.Count,Plans.Count);}
        public void Complete(string requestHash,ReviewCallOutcome outcome,int? inputTokens=null,int? outputTokens=null) { }
    }
    private sealed class SwitchingMeter(Action change):IRequestTokenMeter
    {
        private readonly RequestTokenMeter _inner=new();
        public TokenMeasurement Measure(ModelRouteKey route,ChatRequest request){var m=_inner.Measure(route,request);change();return m;}
        public void RecordUsage(ModelRouteKey route,ChatRequest request,ChatUsage usage)=>_inner.RecordUsage(route,request,usage);
        public void Invalidate(ModelRouteKey route)=>_inner.Invalidate(route);
    }
    private sealed class DeniedGate:IChatDispatchGate
    {public T Dispatch<T>(ChatRequest request,ModelRouteKey route,Func<T> start,CancellationToken token)=>throw new InvalidOperationException("SYNTHETIC_REVOKED");}

    private sealed class Resolver(Action before):IChatProviderResolver
    {
        public int Sends;
        public bool Incomplete;
        private void Before()=>before();
        public IChatProvider Resolve()=>throw new InvalidOperationException("UNGATED_RESOLUTION");
        public IChatProvider Resolve(ModelConnectionSettings connection,IChatDispatchGate gate)=>new Provider(this,gate);
        private sealed class Provider(Resolver owner,IChatDispatchGate gate):IChatProvider
        {
            public string ProviderId=>"deepseek";public string ModelId=>"deepseek-flash";
            public Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken token)=>throw new NotSupportedException();
            public async IAsyncEnumerable<ChatStreamChunk> StreamAsync(ChatRequest request,[EnumeratorCancellation] CancellationToken token)
            {
                gate.Dispatch(request,ModelRouteKey.From(Connection),()=>{owner.Sends++;owner.Before();return true;},token);
                await Task.Yield();yield return new("{}",FinishReason:"stop",Usage:new(123,2));
                if(!owner.Incomplete)yield return new("",IsComplete:true,FinishReason:"stop");
            }
        }
    }
}
