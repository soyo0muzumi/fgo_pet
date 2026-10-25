using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Settings;
using FgoPet.Dialogue.Settings;
using FgoPet.Understanding.Contracts;

namespace FgoPet.Understanding;

public sealed class UnavailableReviewBudget:IReviewBudgetAccess
{public ReviewBudgetUse? Get(ConversationScope scope)=>null;}

public sealed class ConfiguredReviewModelFactory(IDialogueSettingsStore settings,IChatProviderResolver resolver,
    IRequestTokenMeter meter,IReviewBudgetAccess budgets,Action<UnderstandingDiagnostic>? diagnostics=null):IReviewModelFactory
{
    public bool Available(ConversationScope scope)=>settings.Load().ModelConnection is not null&&budgets.Get(scope) is {} use&&use.ScopeKey==scope.Key;
    public IReviewModel Create(ConversationScope scope,UnderstandingState state,IChatDispatchGate admission)
    {
        var (provider,route)=Provider(scope,state,admission,"review");return new UnderstandingReviewModel(provider,route,meter,diagnostics,provider.RequestLimits);
    }
    public async Task<IUnderstandingRoutingPolicy?> ClassifyAsync(ReviewModelInput input,ReviewModelResult result,UnderstandingState state,
        IChatDispatchGate admission,CancellationToken token)
    {
        if(result.Proposals.Count==0||!result.Proposals.Any(UnderstandingSemanticClassifier.Candidate))return null;
        var (provider,route)=Provider(input.Scope,state,admission,"classify");
        return await UnderstandingSemanticClassifier.ClassifyAsync(input,result.Proposals,provider,route,meter,token,provider.RequestLimits).ConfigureAwait(false);
    }
    private (ReviewBudgetedChatProvider Provider,ModelRouteKey Route) Provider(ConversationScope scope,UnderstandingState state,IChatDispatchGate admission,string purpose)
    {
        var connection=settings.Load().ModelConnection??throw new InvalidOperationException("UNDERSTANDING_MODEL_UNAVAILABLE");
        var budget=budgets.Get(scope);
        if(budget is null||budget.ScopeKey!=scope.Key)throw new InvalidOperationException("UNDERSTANDING_BUDGET_UNAVAILABLE");
        var route=ModelRouteKey.From(connection);
        return(new ReviewBudgetedChatProvider(resolver,connection,meter,budget,purpose,state.Policy.Mode,state.Generation,admission,
            ()=>settings.Load().ModelConnection is {} current&&ModelRouteKey.From(current)==route,diagnostics),route);
    }
}
