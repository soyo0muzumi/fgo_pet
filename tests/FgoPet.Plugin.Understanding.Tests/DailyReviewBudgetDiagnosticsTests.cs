using FgoPet.Core.Dialogue;
using FgoPet.Core.Settings;
using FgoPet.Dialogue.Settings;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Understanding;
using FgoPet.Understanding.Contracts;
using Xunit;

namespace FgoPet.Plugin.Understanding.Tests;

public sealed class DailyReviewBudgetDiagnosticsTests
{
    [Fact] public void Budget_reasons_distinguish_policy_model_recipient_and_limit_without_new_reservations()
    {
        var root=Path.Combine(Path.GetTempPath(),"fgo-budget-diagnostics-"+Guid.NewGuid().ToString("N"));
        var db=new RuntimeDatabase(Path.Combine(root,"runtime.db"),pooling:false);new RuntimeDatabaseMigrator(db).Migrate();
        var scope=new ConversationScope("synthetic-role","synthetic-project");
        var owner=new UnderstandingService(db,new ReviewWorkQueueTests.Timeline());var settings=new Settings();var events=new List<UnderstandingDiagnostic>();
        var ledger=new DailyReviewLedger(Path.Combine(root,"budget.db"),TimeProvider.System);
        var access=new DailyReviewBudgetAccess(ledger,settings,owner,events.Add);
        Assert.Null(access.Get(scope));Assert.Equal("UNDERSTANDING_OFF",events[^1].Code);
        Assert.False(File.Exists(Path.Combine(root,"budget.db")));
        owner.Configure(scope,0,new(UnderstandingMode.Proposals,ReadSources:true,ExternalProcessing:true,SaveCandidates:true));
        settings.Connection=null;Assert.Null(access.Get(scope));Assert.Equal("UNDERSTANDING_MODEL_UNAVAILABLE",events[^1].Code);
        settings.Connection=Settings.ConnectionValue;Assert.Null(access.Get(scope));Assert.Equal("UNDERSTANDING_BUDGET_UNAUTHORIZED",events[^1].Code);
        var route=ReviewBudgetedChatProvider.RouteHash(ModelRouteKey.From(settings.Connection));
        ledger.Authorize(scope.Key,route,1);Assert.NotNull(access.Get(scope));Assert.Null(events[^1].Code);
        Assert.Equal(0,ledger.Status(scope.Key,route).Used);
        Assert.NotNull(ledger.TryReserve(new(ReviewBatch.Formal,"daily",scope.Key,"synthetic-reservation",route,"review",
            UnderstandingMode.Proposals,1,new('A',64),100)));
        Assert.Null(access.Get(scope));Assert.Equal("UNDERSTANDING_BUDGET_LIMITED",events[^1].Code);
        Assert.Equal(1,ledger.Status(scope.Key,route).Used);
        settings.Connection=new("synthetic","https://example.invalid/v1","changed-model");Assert.Null(access.Get(scope));
        Assert.Equal("UNDERSTANDING_BUDGET_UNAUTHORIZED",events[^1].Code);
        var throwing=new DailyReviewBudgetAccess(ledger,settings,owner,_=>throw new IOException("private"));
        Assert.Null(throwing.Get(scope));Assert.Equal(1,ledger.Status(scope.Key,route).Used);
    }

    [Fact] public void Corrupt_budget_store_logs_safe_reason_without_exposing_or_replacing_its_content()
    {
        var root=Path.Combine(Path.GetTempPath(),"fgo-budget-diagnostics-"+Guid.NewGuid().ToString("N"));
        var db=new RuntimeDatabase(Path.Combine(root,"runtime.db"),pooling:false);new RuntimeDatabaseMigrator(db).Migrate();
        var path=Path.Combine(root,"budget.db");File.WriteAllText(path,"private corrupt content");
        var events=new List<UnderstandingDiagnostic>();
        var access=new DailyReviewBudgetAccess(new DailyReviewLedger(path,TimeProvider.System),new Settings(),new UnderstandingService(db,new ReviewWorkQueueTests.Timeline()),events.Add);
        var result=access.Status(new("role","project"));
        Assert.Equal("UNDERSTANDING_DAILY_STORE_UNAVAILABLE",result.Code);
        Assert.Equal(result.Code,Assert.Single(events).Code);
        Assert.Equal("private corrupt content",File.ReadAllText(path));
    }
    private sealed class Settings:IDialogueSettingsStore
    {
        public static readonly ModelConnectionSettings ConnectionValue=new("synthetic","https://example.invalid/v1","synthetic-model");
        public ModelConnectionSettings? Connection=ConnectionValue;
        public DialogueSettings Load()=>new(Connection,true);
        public void Save(DialogueSettings settings)=>throw new NotSupportedException();
    }
}
