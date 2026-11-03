using FgoPet.Core.Dialogue;
using FgoPet.Dialogue.Settings;
using FgoPet.Understanding.Contracts;

namespace FgoPet.Understanding;

public sealed record UnderstandingAutomationStatus(string? RouteHash, DailyReviewStatus? Budget, string? Code);

/// <summary>The host grant is bound to the recipient; policy remains independently owned by Understanding.</summary>
public sealed class DailyReviewBudgetAccess(DailyReviewLedger ledger, IDialogueSettingsStore settings,
    UnderstandingService owner, Action<UnderstandingDiagnostic>? diagnostics = null) : IReviewBudgetAccess
{
    private void Log(ConversationScope scope,string? code)=>UnderstandingDiagnostics.Write(diagnostics,TimeProvider.System,scope.Key,"daily_budget_checked",code);
    public UnderstandingAutomationStatus Status(ConversationScope scope)
    {
        var connection = settings.Load().ModelConnection;
        if (connection is null) { Log(scope,"UNDERSTANDING_MODEL_UNAVAILABLE");return new(null, null, "UNDERSTANDING_MODEL_UNAVAILABLE"); }
        var route = ReviewBudgetedChatProvider.RouteHash(ModelRouteKey.From(connection));
        try { var budget = ledger.Status(scope.Key, route);Log(scope,budget.Code);return new(route, budget, budget.Code); }
        catch { Log(scope,"UNDERSTANDING_DAILY_STORE_UNAVAILABLE");return new(route, null, "UNDERSTANDING_DAILY_STORE_UNAVAILABLE"); }
    }

    public ReviewBudgetUse? Get(ConversationScope scope)
    {
        var policy = owner.State(scope).Policy;
        if (!policy.Enabled || !policy.ReadSources || !policy.ExternalProcessing || !policy.SaveCandidates || policy.Mode == UnderstandingMode.Off)
        {
            Log(scope,!policy.Enabled?"UNDERSTANDING_DISABLED":policy.Mode==UnderstandingMode.Off?"UNDERSTANDING_OFF":
                !policy.ReadSources?"UNDERSTANDING_READ_DENIED":!policy.ExternalProcessing?"UNDERSTANDING_EXTERNAL_DENIED":"UNDERSTANDING_SAVE_DENIED");
            return null;
        }
        var status = Status(scope);
        if (status.Code is not null) return null;
        return new(ledger, ReviewBatch.Formal, "daily", scope.Key);
    }

    public void Configure(ConversationScope scope, long revision, UnderstandingPolicy policy, long sourceBoundary,
        string routeHash, int dailyLimit, CancellationToken token)
    {
        DailyReviewLedger.ValidateLimit(dailyLimit);
        if (policy.Mode is not (UnderstandingMode.Proposals or UnderstandingMode.Bounded) || !policy.Enabled ||
            !policy.ReadSources || !policy.ExternalProcessing || !policy.SaveCandidates)
            throw new ArgumentException("UNDERSTANDING_AUTOMATION_GRANTS_REQUIRED");
        var status = Status(scope);
        if (status.RouteHash != routeHash || status.Budget is null) throw new InvalidOperationException("UNDERSTANDING_MODEL_CHANGED");
        token.ThrowIfCancellationRequested();
        owner.Configure(scope, revision, policy, sourceBoundary, token);
        token.ThrowIfCancellationRequested();
        // Caller holds the dialogue suspension; no background dispatch can race these two stores.
        ledger.Authorize(scope.Key, routeHash, dailyLimit);
    }

    public void RevokeIfRequired(ConversationScope scope, UnderstandingPolicy policy)
    {
        if (policy.Mode == UnderstandingMode.Off || !policy.ReadSources || !policy.ExternalProcessing || !policy.SaveCandidates)
            ledger.Revoke(scope.Key);
    }
}
