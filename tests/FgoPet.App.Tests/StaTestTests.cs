using System.Windows;
using System.Windows.Threading;
using Xunit;

namespace FgoPet.App.Tests;

public sealed class StaTestTests
{
    [Fact]
    public async Task Application_dispatcher_remains_responsive_after_a_sta_test_returns()
    {
        Dispatcher? dispatcher = null;
        StaTest.Run(() => dispatcher = (Application.Current ?? new Application()).Dispatcher);

        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = dispatcher!.BeginInvoke(() => completed.SetResult());
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
