using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;

namespace FgoPet.App.Tests;

internal static class StaTest
{
    private static readonly Lazy<Dispatcher> TestDispatcher = new(StartDispatcher);
    private static readonly ConcurrentQueue<object> DiagnosticEvents = new();
    private static long _nextCorrelation;
    [ThreadStatic] private static long _activeCorrelation;

    public static void Run(Action action, [CallerMemberName] string member = "",
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        if (!DiagnosticsEnabled)
        {
            TestDispatcher.Value.Invoke(action);
            return;
        }

        var correlation = NewDiagnosticCorrelation();
        var callsite = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFileName(file) + ":" + member + ":" + line)))[..16];
        RecordDiagnostic(correlation, "input_received", callsite);
        var dispatcher = TestDispatcher.Value;
        RecordDiagnostic(correlation, "invoke_queued", callsite);
        try
        {
            dispatcher.Invoke(() =>
            {
                var parent = _activeCorrelation;
                _activeCorrelation = correlation;
                RecordDiagnostic(correlation, "action_started", callsite, parent: parent);
                try
                {
                    action();
                    RecordDiagnostic(correlation, "action_completed", callsite, parent: parent);
                }
                catch (Exception error)
                {
                    RecordDiagnostic(correlation, "action_failed", callsite, error.GetType().Name, parent);
                    throw;
                }
                finally { _activeCorrelation = parent; }
            });
            RecordDiagnostic(correlation, "caller_returned", callsite);
        }
        catch (Exception error)
        {
            RecordDiagnostic(correlation, "caller_failed", callsite, error.GetType().Name);
            throw;
        }
    }

    internal static long NewDiagnosticCorrelation() => Interlocked.Increment(ref _nextCorrelation);

    internal static long ActiveDiagnosticCorrelation => _activeCorrelation;

    private static bool DiagnosticsEnabled => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FGO_PET_STA_DIAGNOSTICS"));

    internal static void RecordCurrentPhase(string phase) => RecordDiagnostic(_activeCorrelation, phase);

    internal static void RecordDiagnostic(long correlation, string phase, string? callsite = null,
        string result = "observed", long parent = 0)
    {
        if (!DiagnosticsEnabled) return;
        DiagnosticEvents.Enqueue(new
        {
            atUtc = DateTimeOffset.UtcNow,
            tick = Stopwatch.GetTimestamp(),
            frequency = Stopwatch.Frequency,
            thread = Environment.CurrentManagedThreadId,
            correlation,
            parent,
            phase,
            result,
            callsite,
            threadPoolThreads = ThreadPool.ThreadCount,
            pendingThreadPoolItems = ThreadPool.PendingWorkItemCount,
        });
    }

    // Flush outside the dispatcher after the selected tests finish; no file I/O in timed actions.
    internal static void FlushDiagnostics()
    {
        var directory = Environment.GetEnvironmentVariable("FGO_PET_STA_DIAGNOSTICS");
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory) || !Directory.Exists(directory)) return;
        try
        {
            using var writer = File.AppendText(Path.Combine(directory, "sta-events.jsonl"));
            while (DiagnosticEvents.TryDequeue(out var item)) writer.WriteLine(JsonSerializer.Serialize(item));
            ComponentTimingProbe.FlushDiagnostics(directory);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static Dispatcher StartDispatcher()
    {
        // Application.Current can outlive an individual test. Its owning thread
        // must keep pumping until the test host exits, including between tests.
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                RecordDiagnostic(0, "dispatcher_thread_started");
                var dispatcher = Dispatcher.CurrentDispatcher;
                _ = dispatcher.BeginInvoke(() =>
                {
                    RecordDiagnostic(0, "dispatcher_ready");
                    ready.SetResult(dispatcher);
                });
                Dispatcher.Run();
            }
            catch (Exception error) { ready.TrySetException(error); }
        }) { IsBackground = true, Name = "FgoPet App test dispatcher" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task.GetAwaiter().GetResult();
    }
}
