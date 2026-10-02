using System.Windows.Threading;

namespace FgoPet.App.Tests;

internal static class StaTest
{
    private static readonly Lazy<Dispatcher> TestDispatcher = new(StartDispatcher);

    public static void Run(Action action) => TestDispatcher.Value.Invoke(action);

    private static Dispatcher StartDispatcher()
    {
        // Application.Current can outlive an individual test. Its owning thread
        // must keep pumping until the test host exits, including between tests.
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                _ = dispatcher.BeginInvoke(() => ready.SetResult(dispatcher));
                Dispatcher.Run();
            }
            catch (Exception error) { ready.TrySetException(error); }
        }) { IsBackground = true, Name = "FgoPet App test dispatcher" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task.GetAwaiter().GetResult();
    }
}
