using System.Diagnostics;
using System.Text.Json;

namespace FgoPet.Windows.Tests;

/// <summary>Test-only parent/child payload. No shell initialization is required before the original two-second deadline.</summary>
internal static class BoundedProcessFixtureHost
{
    public static int Main(string[] args)
    {
        if (args is not ["--bounded-process-fixture", "parent" or "delayed-parent" or "child", var id] ||
            id.Length != 32 || id.Any(c => !char.IsAsciiHexDigit(c))) return 2;
        var watch = Stopwatch.StartNew();
        void Log(string phase) => Console.Error.WriteLine(JsonSerializer.Serialize(new {
            atUtc = DateTimeOffset.UtcNow, correlation = id[..24].ToUpperInvariant(), phase,
            tick = Stopwatch.GetTimestamp(), elapsedMs = watch.Elapsed.TotalMilliseconds, result = "started" }));
        if (args[1] == "child")
        {
            Log("child_ready");
            Thread.Sleep(Timeout.Infinite);
            return 0;
        }
        Log("parent_ready");
        // Negative control: timeout without a created descendant is not descendant cleanup evidence.
        if (args[1] == "delayed-parent") Thread.Sleep(TimeSpan.FromSeconds(30));
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var item in new[] { typeof(BoundedProcessFixtureHost).Assembly.Location, "--bounded-process-fixture", "child", id })
            start.ArgumentList.Add(item);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("FIXTURE_CHILD_START_FAILED");
        Console.Out.Write(child.Id); // Private runner result, not public diagnostics.
        Console.Out.Flush();
        Log("child_started");
        child.WaitForExit();
        return child.ExitCode;
    }
}
