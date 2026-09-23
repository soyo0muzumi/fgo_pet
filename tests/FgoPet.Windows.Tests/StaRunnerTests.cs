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
            await Task.Yield();
            dispatcher = Dispatcher.CurrentDispatcher;
        });

        Assert.NotNull(dispatcher);
        Assert.True(dispatcher.HasShutdownFinished);
        Assert.False(dispatcher.Thread.IsAlive);
    }
}
