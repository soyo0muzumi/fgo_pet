using FgoPet.Understanding;
using FgoPet.Understanding.Contracts;
using Xunit;

namespace FgoPet.Plugin.Understanding.Tests;

public sealed class ReviewCallLedgerTests
{
    private static readonly string Route = new('A',64);
    private static readonly string Approval = new('B',64);
    private static ReviewCallPlan Call(string id, ReviewBatch batch=ReviewBatch.Formal, string trial="R1-1") =>
        new(batch,trial,"synthetic-scope",id,Route,"review",UnderstandingMode.Proposals,7,new('C',64),1000);
    private static string Store() => Path.Combine(Path.GetTempPath(),"fgo-review-ledger-"+Guid.NewGuid().ToString("N"),"calls.db");
    private static ReviewCallLedger Approved(string path,int smoke=24,int formal=114,int repair=114,int total=252)
    {
        var ledger=new ReviewCallLedger(path);
        ledger.Approve(new("synthetic-approved-task",Route,Approval,true,smoke,formal,repair,total));
        return ledger;
    }

    [Fact]
    public void No_approval_or_unregistered_trial_never_reserves_a_call()
    {
        var path=Store(); var denied=new ReviewCallLedger(path);
        Assert.Null(denied.TryReserve(Call("one")));
        Assert.Null(Approved(path).TryReserve(Call("two")));
        Assert.Equal(0,denied.Snapshot().TotalUsed);
    }

    [Fact]
    public void An_unfinished_dispatch_stays_charged_after_reopening_the_store()
    {
        var path=Store(); var first=Approved(path);first.RegisterTrial(ReviewBatch.Formal,"R1-1","synthetic-scope",6);
        Assert.NotNull(first.TryReserve(Call("one")));
        var second=Approved(path);second.RegisterTrial(ReviewBatch.Formal,"R1-1","synthetic-scope",6);
        var snapshot=second.Snapshot(); Assert.Equal(1,snapshot.TotalUsed);
        Assert.Equal(ReviewCallOutcome.ReservedOrUnknown,Assert.Single(snapshot.Calls).Outcome);
        Assert.Null(second.TryReserve(Call("one"))); // Reusing a request never buys another dispatch.
        var next=Assert.IsType<ReviewCallReservation>(second.TryReserve(Call("two")));
        Assert.Equal(2,next.TrialUsed);
    }

    [Fact]
    public void Batch_and_trial_limits_cannot_borrow_unused_smoke_or_repair_calls()
    {
        var ledger=Approved(Store(),smoke:2,formal:2,repair:2,total:6);
        ledger.RegisterTrial(ReviewBatch.Formal,"R1-1","synthetic-scope",1);
        ledger.RegisterTrial(ReviewBatch.Formal,"R1-2","synthetic-scope",6);
        ledger.RegisterTrial(ReviewBatch.Repair,"R1-1","synthetic-scope",6);
        Assert.NotNull(ledger.TryReserve(Call("a")));
        Assert.Null(ledger.TryReserve(Call("b")));
        Assert.NotNull(ledger.TryReserve(Call("c",trial:"R1-2")));
        Assert.Null(ledger.TryReserve(Call("d",trial:"R1-2")));
        Assert.NotNull(ledger.TryReserve(Call("e",ReviewBatch.Repair)));
        Assert.Equal(3,ledger.Snapshot().TotalUsed);
    }

    [Fact]
    public void Failure_keeps_the_reservation_and_records_actual_usage_without_refund()
    {
        var ledger=Approved(Store());ledger.RegisterTrial(ReviewBatch.Formal,"R1-1","synthetic-scope",6);
        var charge=Assert.IsType<ReviewCallReservation>(ledger.TryReserve(Call("one")));
        ledger.Complete(charge.RequestHash,ReviewCallOutcome.Failed,1002,70);
        var row=Assert.Single(ledger.Snapshot().Calls);
        Assert.Equal(1,ledger.Snapshot().TotalUsed);Assert.Equal(1002,row.InputTokens);Assert.Equal(70,row.OutputTokens);
        Assert.Equal(ReviewCallOutcome.Failed,row.Outcome);
    }

    [Fact]
    public async Task Separate_ledger_instances_cannot_race_past_one_trial_limit()
    {
        var path=Store();var first=Approved(path);var second=Approved(path);
        first.RegisterTrial(ReviewBatch.Formal,"R1-1","synthetic-scope",6);
        var results=await Task.WhenAll(Enumerable.Range(0,20).Select(i=>Task.Run(()=>(i%2==0?first:second).TryReserve(Call("parallel-"+i)))));
        Assert.Equal(6,results.Count(r=>r is not null));Assert.Equal(6,new ReviewCallLedger(path).Snapshot().TotalUsed);
    }

    [Fact]
    public void Total_limit_stops_a_fresh_batch_even_if_its_own_balance_is_unused()
    {
        var ledger=Approved(Store(),2,2,2,2);
        ledger.RegisterTrial(ReviewBatch.Smoke,"smoke-1","synthetic-scope",2);
        ledger.RegisterTrial(ReviewBatch.Formal,"R1-1","synthetic-scope",2);
        ledger.RegisterTrial(ReviewBatch.Repair,"R1-1","synthetic-scope",2);
        Assert.NotNull(ledger.TryReserve(Call("a",ReviewBatch.Smoke,"smoke-1")));
        Assert.NotNull(ledger.TryReserve(Call("b")));
        Assert.Null(ledger.TryReserve(Call("c",ReviewBatch.Repair)));
        Assert.Equal(2,ledger.Snapshot().TotalUsed);
    }

    [Fact]
    public void Route_scope_and_complete_input_limit_are_checked_before_charging()
    {
        var ledger=Approved(Store());ledger.RegisterTrial(ReviewBatch.Formal,"R1-1","synthetic-scope",6);
        Assert.Null(ledger.TryReserve(Call("route") with {RouteHash=new('D',64)}));
        Assert.Null(ledger.TryReserve(Call("scope") with {ScopeKey="another-scope"}));
        Assert.Null(ledger.TryReserve(Call("input") with {EstimatedInputTokens=16001}));
        Assert.Equal(0,ledger.Snapshot().TotalUsed);
        Assert.NotNull(ledger.TryReserve(Call("edge") with {EstimatedInputTokens=16000}));
    }

    [Fact]
    public void Reapproval_cannot_reset_charges_or_create_another_budget_identity()
    {
        var path=Store();var ledger=Approved(path);ledger.RegisterTrial(ReviewBatch.Formal,"R1-1","synthetic-scope",6);
        Assert.NotNull(ledger.TryReserve(Call("one")));
        Assert.Throws<InvalidOperationException>(()=>ledger.Approve(new("another-task",Route,Approval,true)));
        Assert.Throws<InvalidOperationException>(()=>ledger.Approve(new("synthetic-approved-task",Route,Approval,true,TotalLimit:251)));
        Assert.Equal(1,Approved(path).Snapshot().TotalUsed);
    }

    [Fact]
    public void Unknown_result_is_retained_and_terminal_accounting_cannot_be_rewritten()
    {
        var path=Store();var ledger=Approved(path);ledger.RegisterTrial(ReviewBatch.Formal,"R1-1","synthetic-scope",6);
        var charge=Assert.IsType<ReviewCallReservation>(ledger.TryReserve(Call("one")));
        ledger.Complete(charge.RequestHash,ReviewCallOutcome.Unknown);
        ledger.Complete(charge.RequestHash,ReviewCallOutcome.Completed,100,10);
        var row=Assert.Single(Approved(path).Snapshot().Calls);
        Assert.Equal(ReviewCallOutcome.Unknown,row.Outcome);Assert.Null(row.InputTokens);
    }

    [Fact]
    public void R3B_and_trial_identity_limits_cannot_be_relabelled()
    {
        var ledger=Approved(Store());
        Assert.Throws<ArgumentException>(()=>ledger.RegisterTrial(ReviewBatch.Formal,"R3B-1","synthetic-scope",3));
        Assert.Throws<ArgumentException>(()=>ledger.RegisterTrial(ReviewBatch.Formal,"R1-4","synthetic-scope",6));
        ledger.RegisterTrial(ReviewBatch.Formal,"R3B-1","synthetic-scope",2);
        Assert.Throws<InvalidOperationException>(()=>ledger.RegisterTrial(ReviewBatch.Formal,"R3B-1","another-scope",2));
    }

    [Fact]
    public void A_nonledger_database_is_rejected_without_replacing_its_contents()
    {
        var path=Store();Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using(var db=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+path+";Pooling=False"))
        {
            db.Open();using var command=db.CreateCommand();command.CommandText="CREATE TABLE fixture(value INTEGER);INSERT INTO fixture VALUES(42)";
            command.ExecuteNonQuery();
        }
        Assert.Throws<InvalidOperationException>(()=>new ReviewCallLedger(path).Snapshot());
        using var reopened=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+path+";Pooling=False");reopened.Open();
        using var read=reopened.CreateCommand();read.CommandText="SELECT value FROM fixture";Assert.Equal(42L,read.ExecuteScalar());
    }
}
