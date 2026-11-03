using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Settings;
using FgoPet.Dialogue.Settings;
using FgoPet.Understanding.Contracts;

namespace FgoPet.Understanding;

public sealed class UnavailableReviewBudget:IReviewBudgetAccess
{public ReviewBudgetUse? Get(ConversationScope scope)=>null;}

public sealed class ConfiguredReviewModelFactory(IDialogueSettingsStore settings,IChatProviderResolver resolver,
    IRequestTokenMeter meter,IReviewBudgetAccess budgets,Action<UnderstandingDiagnostic>? diagnostics=null,
    IModelContextResolver? contexts=null):IReviewModelFactory
{
    public bool Available(ConversationScope scope)
    {
        if(settings.Load().ModelConnection is null)
        {
            UnderstandingDiagnostics.Write(diagnostics,TimeProvider.System,scope.Key,"model_availability_checked","UNDERSTANDING_MODEL_UNAVAILABLE");
            return false;
        }
        var use=budgets.Get(scope);
        var available=use is not null&&use.ScopeKey==scope.Key;
        UnderstandingDiagnostics.Write(diagnostics,TimeProvider.System,scope.Key,"model_availability_checked",
            available?null:use is null?"UNDERSTANDING_BUDGET_UNAVAILABLE":"UNDERSTANDING_BUDGET_SCOPE_MISMATCH");
        return available;
    }
    public IReviewModel Create(ConversationScope scope,UnderstandingState state,IChatDispatchGate admission)
    {
        if(contexts is not null)return new DeferredModel(async (input,token)=>
        {
            var limits=await LimitsAsync(scope,token).ConfigureAwait(false);input.EnsureCurrent(token);
            var (provider,route)=Provider(scope,state,admission,"review",limits);
            return await new UnderstandingReviewModel(provider,route,meter,diagnostics,provider.RequestLimits).ReviewAsync(input,token).ConfigureAwait(false);
        });
        var (provider,route)=Provider(scope,state,admission,"review");return new UnderstandingReviewModel(provider,route,meter,diagnostics,provider.RequestLimits);
    }
    public async Task<IUnderstandingRoutingPolicy?> ClassifyAsync(ReviewModelInput input,ReviewModelResult result,UnderstandingState state,
        IChatDispatchGate admission,CancellationToken token)
    {
        if(result.Proposals.Count==0||!result.Proposals.Any(p=>p.Origin is not null||UnderstandingSemanticClassifier.Candidate(p)))return null;
        (ModelRouteKey Route,ReviewRequestLimits Limits)? limits=contexts is null?null:await LimitsAsync(input.Scope,token).ConfigureAwait(false);
        input.EnsureCurrent(token);
        var (provider,route)=Provider(input.Scope,state,admission,"classify",limits);
        return await UnderstandingSemanticClassifier.ClassifyAsync(input,result.Proposals,provider,route,meter,token,provider.RequestLimits).ConfigureAwait(false);
    }
    public IRecurrenceModel CreateRecurrence(ConversationScope scope,UnderstandingState state,IChatDispatchGate admission)
        =>new DeferredRecurrence(async token=>
        {
            (ModelRouteKey Route,ReviewRequestLimits Limits)? limits=contexts is null?null:await LimitsAsync(scope,token).ConfigureAwait(false);
            // Discovery consumes an ordinary granted review dispatch; historical immutable ledgers are unchanged.
            var (provider,route)=Provider(scope,state,admission,"review",limits);
            return new RecurrenceReliabilityModel(provider,route,meter,provider.RequestLimits);
        });
    private sealed class DeferredRecurrence(Func<CancellationToken,Task<RecurrenceReliabilityModel>> resolve):IRecurrenceModel
    {
        public async Task<RecurrenceDiscovery> DiscoverAsync(ReviewModelInput input,IReadOnlyList<RecurrenceClue> clues,CancellationToken token=default)
        {input.EnsureCurrent(token);return await (await resolve(token).ConfigureAwait(false)).DiscoverAsync(input,clues,token).ConfigureAwait(false);}
        public async Task<RecurrenceSelection> RetrieveAsync(ReviewModelInput input,IReadOnlyList<RecurrenceClue> clues,RecurrenceDiscovery discovery,CancellationToken token=default)
        {input.EnsureCurrent(token);return await (await resolve(token).ConfigureAwait(false)).RetrieveAsync(input,clues,discovery,token).ConfigureAwait(false);}
    }
    private async Task<(ModelRouteKey Route,ReviewRequestLimits Limits)> LimitsAsync(ConversationScope scope,CancellationToken token)
    {
        if(!Available(scope))throw new InvalidOperationException("UNDERSTANDING_BUDGET_UNAVAILABLE");
        var connection=settings.Load().ModelConnection??throw new InvalidOperationException("UNDERSTANDING_MODEL_UNAVAILABLE");
        var limit=await contexts!.ResolveAsync(connection,token).ConfigureAwait(false);
        var route=ModelRouteKey.From(connection);
        if(limit.Route!=route)throw new InvalidOperationException("UNDERSTANDING_MODEL_CHANGED");
        var output=Math.Min(connection.MaxOutputTokens,limit.MaxOutputTokens??connection.MaxOutputTokens);
        var budget=PromptBudget.Resolve(limit,output);
        return(route,new(budget.InputTokens,output));
    }
    private sealed class DeferredModel(Func<ReviewModelInput,CancellationToken,Task<ReviewModelResult>> run):IReviewModel
    {public Task<ReviewModelResult> ReviewAsync(ReviewModelInput input,CancellationToken cancellationToken=default)=>run(input,cancellationToken);}
    private (ReviewBudgetedChatProvider Provider,ModelRouteKey Route) Provider(ConversationScope scope,UnderstandingState state,IChatDispatchGate admission,string purpose,
        (ModelRouteKey Route,ReviewRequestLimits Limits)? resolved=null)
    {
        var connection=settings.Load().ModelConnection??throw new InvalidOperationException("UNDERSTANDING_MODEL_UNAVAILABLE");
        var budget=budgets.Get(scope);
        if(budget is null||budget.ScopeKey!=scope.Key)throw new InvalidOperationException("UNDERSTANDING_BUDGET_UNAVAILABLE");
        var route=ModelRouteKey.From(connection);
        if(resolved is {} capacity)
        {
            if(capacity.Route!=route)throw new InvalidOperationException("UNDERSTANDING_MODEL_CHANGED");
            budget=budget with{RequestLimits=capacity.Limits};
        }
        return(new ReviewBudgetedChatProvider(resolver,connection,meter,budget,purpose,state.Policy.Mode,state.Generation,admission,
            ()=>settings.Load().ModelConnection is {} current&&ModelRouteKey.From(current)==route,diagnostics),route);
    }
}
