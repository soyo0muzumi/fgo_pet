using System.IO;
using System.Text.Json;
using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Understanding;
using Microsoft.Extensions.DependencyInjection;

namespace FgoPet.Testing.Understanding;

internal static class ProposalIsolationRegression
{
    internal static async Task<int> RunAsync(string repository,string report)
    {
        var stage=SubscriptionOptions.Stage(report);var connection=SubscriptionOptions.ReadConnection(stage);
        var limits=SubscriptionOptions.Capacity(connection);var id=Guid.NewGuid().ToString("N");
        var directory=Path.Combine(stage,"proposal-regression-"+id);Directory.CreateDirectory(directory);
        ValidationHost.Write(Path.Combine(directory,"state-root.json"),new{container="fgo-p4-buddy",id,trial="R4-1"});
        using var ledger=new SubscriptionCallLedger(Path.Combine(directory,"dispatch-events.jsonl"),
            ReviewBudgetedChatProvider.RouteHash(ModelRouteKey.From(connection)),limits.MaxInputTokens);
        var options=new SubscriptionOptions(connection,limits,ledger);var fixture=TrialFixtures.Formal().Single(f=>f.Id=="R4-1");
        var liveRefused=false;
        try{await using var unused=await ValidationHost.CreateSubscriptionAsync(repository,directory,fixture,options,true,proposalRegression:true);}
        catch(InvalidOperationException error)when(error.Message=="VALIDATION_LIVE_PROPOSAL_REGRESSION_DENIED"){liveRefused=true;}
        Require(liveRefused&&ledger.Snapshot().TotalUsed==0,"VALIDATION_REGRESSION_LIVE_GUARD_FAILED");
        await using var host=await ValidationHost.CreateSubscriptionAsync(repository,directory,fixture,options,false,proposalRegression:true);
        host.Prepare(fixture);
        var sent=await host.SendAsync(fixture.Inputs[0]);
        Require(sent.Status==ConversationSendStatus.Completed&&host.RegressionReadDenialsObserved,"VALIDATION_DRAFT_REPLY_MISSING");
        var drafts=host.Services.GetRequiredService<ITodoDraftWorkflow>();
        var draft=drafts.Get(sent.ConversationId,ValidationHost.Role);
        Require(draft is{Status:TodoDraftStatus.Pending}&&draft.Proposals.Count==1&&draft.CreatedItemId is null,"VALIDATION_PENDING_DRAFT_MISSING");
        var canary=host.ValidationContributions.Tools.Single(t=>t.Descriptor.Name=="validation.unrelated");
        var denied=await canary.InvokeAsync(new(new(sent.ConversationId,ValidationHost.Role,host.Scope.ProjectId),JsonSerializer.SerializeToElement(new{})),default);
        Require(!denied.Success&&denied.ErrorCode=="VALIDATION_TOOL_NOT_AUTHORIZED"&&host.IsolationCanaryCalls==0,"VALIDATION_CANARY_EXECUTED");
        Require(!host.ValidationContributions.Continuations.Any(),"VALIDATION_CONFIRMATION_CONTINUATION_EXPOSED");
        var confirmation=await host.SendAsync("确认");
        Require(confirmation.Status==ConversationSendStatus.Completed&&drafts.Get(sent.ConversationId,ValidationHost.Role)?.Status==TodoDraftStatus.Pending,
            "VALIDATION_DRAFT_CONFIRMATION_EXECUTED");
        using var database=host.Services.GetRequiredService<RuntimeDatabase>().Open();using var command=database.CreateCommand();
        command.CommandText="SELECT COUNT(*) FROM todo_items";
        Require(Convert.ToInt64(command.ExecuteScalar())==0,"VALIDATION_TODO_PERSISTED");
        Require(ledger.Snapshot().TotalUsed==3,"VALIDATION_REGRESSION_ACCOUNTING_MISMATCH");
        host.SaveEvidence("proposal-isolation-regression");
        var result=new{stage="proposal_isolation_regression_passed",liveRegressionRefused=liveRefused,readDenialsPreserved=true,
            pendingDraftCount=1,confirmationExposed=false,canaryInvocations=host.IsolationCanaryCalls,todoRows=0,syntheticProviderCalls=3,realProviderCalls=0};
        ValidationHost.Write(Path.Combine(directory,"result.json"),result);Console.WriteLine(JsonSerializer.Serialize(result));return 0;
    }
    private static void Require(bool condition,string code){if(!condition)throw new InvalidOperationException(code);}
}
