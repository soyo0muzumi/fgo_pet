using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace FgoPet.App.Tests;

/// <summary>Test-only process host. Exercise the actual helper without another test's WPF workload.</summary>
internal static class StaLifetimeProbeHost
{
    public static async Task<int> Main(string[] args)
    {
        if (args is not ["--sta-lifetime-probe", "healthy" or "stopped"]) return 2;
        var correlation = Guid.NewGuid().ToString("N");
        var watch = Stopwatch.StartNew();
        void Log(string phase) => Console.WriteLine(JsonSerializer.Serialize(new
        {
            atUtc = DateTimeOffset.UtcNow, tick = Stopwatch.GetTimestamp(), frequency = Stopwatch.Frequency,
            correlation, phase,
        }));
        int Result(bool passed, string? code)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                atUtc = DateTimeOffset.UtcNow, tick = Stopwatch.GetTimestamp(), frequency = Stopwatch.Frequency,
                correlation, phase = "result", passed, deadlineSeconds = 5, elapsedMs = watch.Elapsed.TotalMilliseconds, code,
            }));
            return passed ? 0 : 1;
        }

        Log("input_received");
        try
        {
            Dispatcher? dispatcher = null;
            StaTest.Run(() => dispatcher = (Application.Current ?? new Application()).Dispatcher);
            Log("dispatcher_ready_after_run_returned");
            if (args[1] == "stopped")
            {
                // Negative control only: a dead owning dispatcher must still fail the unchanged deadline.
                StaTest.Run(() => dispatcher!.BeginInvokeShutdown(DispatcherPriority.Send));
                Log("shutdown_requested");
            }
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Log("heartbeat_queued");
            _ = dispatcher!.BeginInvoke(() =>
            {
                Log("heartbeat_started");
                completed.SetResult();
            });
            try
            {
                await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Log("heartbeat_observed");
                return Result(true, null);
            }
            catch (TimeoutException) { return Result(false, "STA_HEARTBEAT_TIMEOUT"); }
        }
        catch (Exception) { return Result(false, "STA_LIFETIME_SETUP_FAILED"); }
    }
}
