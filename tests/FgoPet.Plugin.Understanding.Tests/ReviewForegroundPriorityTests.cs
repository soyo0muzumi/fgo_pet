using FgoPet.App.Dialogue;
using Xunit;

namespace FgoPet.Plugin.Understanding.Tests;

public sealed class ReviewForegroundPriorityTests
{
    [Fact] public void Foreground_preempts_review_without_waiting_for_its_cancellation_callback()
    {
        var lifetime=new DialogueContextLifetime();using var review=lifetime.TryAcquireBackground(default);
        Assert.NotNull(review);
        using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
        using var callback=review.Token.Register(()=>{entered.Set();release.Wait(TimeSpan.FromSeconds(5));});
        try
        {
            var elapsed=System.Diagnostics.Stopwatch.StartNew();
            using var foreground=lifetime.Acquire(default);
            Assert.True(elapsed.Elapsed<TimeSpan.FromSeconds(2),"Foreground waited for a background cancellation callback.");
            Assert.True(review.Token.IsCancellationRequested);
            Assert.Null(lifetime.TryAcquireBackground(default));
            Assert.Throws<OperationCanceledException>(()=>review.Commit<bool>(()=>throw new Exception("late commit")));
            foreground.CheckCurrent();
        }
        finally{release.Set();}
    }
    [Fact] public async Task Maintenance_drains_background_work_and_process_stop_denies_new_review()
    {
        using var stopping=new CancellationTokenSource();var lifetime=new DialogueContextLifetime(stopping.Token);
        var review=lifetime.TryAcquireBackground(default);Assert.NotNull(review);
        var suspended=lifetime.SuspendAsync(default);
        Assert.True(review.Token.IsCancellationRequested);Assert.False(suspended.IsCompleted);
        review.Dispose();using(await suspended.WaitAsync(TimeSpan.FromSeconds(5)))Assert.Null(lifetime.TryAcquireBackground(default));
        using(var next=lifetime.TryAcquireBackground(default))Assert.NotNull(next);
        stopping.Cancel();Assert.Throws<OperationCanceledException>(()=>lifetime.TryAcquireBackground(default));
    }
}
