using System.Windows.Threading;
using Xunit;

namespace FgoPet.Windows.Tests;

public sealed class StaRunnerTests
{
    [Fact]
    public void Run_finishes_dispatcher_shutdown_before_its_thread_exits()
    {
        Dispatcher? dispatcher = null;
        StaRunner.Run(() => dispatcher = Dispatcher.CurrentDispatcher);

        Assert.NotNull(dispatcher);
        Assert.True(dispatcher.HasShutdownFinished);
        Assert.False(dispatcher.Thread.IsAlive);
    }

    [Fact]
    public async Task RunAsync_finishes_dispatcher_shutdown_after_the_continuation()
    {
        Dispatcher? dispatcher = null;
        await StaRunner.RunAsync(async () =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            var threadId = Environment.CurrentManagedThreadId;
            await Task.Yield();
            Assert.Same(dispatcher, Dispatcher.CurrentDispatcher);
            Assert.Equal(threadId, Environment.CurrentManagedThreadId);
            Assert.IsType<DispatcherSynchronizationContext>(SynchronizationContext.Current);
        });

        Assert.NotNull(dispatcher);
        Assert.True(dispatcher.HasShutdownFinished);
        Assert.False(dispatcher.Thread.IsAlive);
    }

    [Fact]
    public async Task RunAsync_returns_before_work_needing_caller_progress_finishes()
    {
        var started = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher? dispatcher = null;
        var run = StaRunner.RunAsync(async () =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            started.TrySetResult(null);
            await release.Task;
            Assert.Same(dispatcher, Dispatcher.CurrentDispatcher);
        });

        try
        {
            await started.Task.WaitAsync(StaRunner.DefaultTimeout);
            Assert.False(run.IsCompleted);
        }
        finally
        {
            // This line cannot run if RunAsync synchronously joins its STA thread.
            release.TrySetResult(null);
            await run;
        }

        Assert.NotNull(dispatcher);
        Assert.True(dispatcher.HasShutdownFinished);
        Assert.False(dispatcher.Thread.IsAlive);
    }

    [Fact]
    public void Run_preserves_action_failure_and_still_finishes_shutdown()
    {
        var expected = new InvalidOperationException("fixture-action-failed");
        Dispatcher? dispatcher = null;
        var actual = Assert.Throws<InvalidOperationException>(() => StaRunner.Run(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            throw expected;
        }));

        Assert.Same(expected, actual);
        Assert.NotNull(dispatcher);
        Assert.True(dispatcher.HasShutdownFinished);
        Assert.False(dispatcher.Thread.IsAlive);
    }

    [Fact]
    public async Task RunAsync_preserves_async_failure_and_still_waits_for_thread_exit()
    {
        var expected = new InvalidOperationException("fixture-async-action-failed");
        Dispatcher? dispatcher = null;
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => StaRunner.RunAsync(async () =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            await Task.Yield();
            throw expected;
        }));

        Assert.Same(expected, actual);
        Assert.NotNull(dispatcher);
        Assert.True(dispatcher.HasShutdownFinished);
        Assert.False(dispatcher.Thread.IsAlive);
    }
}
