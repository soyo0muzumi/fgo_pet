using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Xunit;

namespace FgoPet.App.Tests;

public sealed class StaTestTests
{
    [Fact]
    public async Task Isolated_lifetime_probe_progresses_while_parent_dispatcher_is_busy()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var busy = Task.Run(() => StaTest.Run(() => { entered.SetResult(); release.Wait(); }));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30)); // Fixture readiness, not the heartbeat deadline.
            var result = await RunIsolatedAsync("healthy");
            Assert.Equal(0, result.ExitCode);
            Assert.True(result.Passed);
            Assert.Equal(5, result.DeadlineSeconds);
            Assert.False(busy.IsCompleted);
        }
        finally
        {
            release.Set();
            await busy.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    [Fact]
    public async Task Isolated_lifetime_probe_rejects_a_stopped_dispatcher_with_the_original_deadline()
    {
        var result = await RunIsolatedAsync("stopped");
        Assert.Equal(1, result.ExitCode);
        Assert.False(result.Passed);
        Assert.Equal(5, result.DeadlineSeconds);
        Assert.Equal("STA_HEARTBEAT_TIMEOUT", result.Code);
    }

    [Fact]
    public async Task Application_dispatcher_remains_responsive_after_a_sta_test_returns()
    {
        var result = await RunIsolatedAsync("healthy");
        Assert.Equal(0, result.ExitCode);
        Assert.True(result.Passed);
        Assert.Equal(5, result.DeadlineSeconds);
    }

    private static async Task<LifetimeResult> RunIsolatedAsync(string scenario)
    {
        var configured = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        var start = new ProcessStartInfo
        {
            FileName = !string.IsNullOrWhiteSpace(configured) && File.Exists(configured) ? configured : "dotnet",
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.ArgumentList.Add(typeof(StaTest).Assembly.Location);
        start.ArgumentList.Add("--sta-lifetime-probe");
        start.ArgumentList.Add(scenario);
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("FGO_PET_", StringComparison.Ordinal)).ToArray())
            start.Environment.Remove(key);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("STA_PROBE_START_FAILED");
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(watchdog.Token);
            var error = process.StandardError.ReadToEndAsync(watchdog.Token);
            await process.WaitForExitAsync(watchdog.Token);
            var lines = (await output).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            await error; // Drain both owned pipes; never put terminal text in assertion diagnostics.
            var final = lines.LastOrDefault(line => line.Contains("\"phase\":\"result\"", StringComparison.Ordinal));
            Assert.True(final is not null, "STA_LIFETIME_PROBE_NO_RESULT");
            using var document = JsonDocument.Parse(final!);
            var root = document.RootElement;
            Console.WriteLine("STA_LIFETIME_METADATA_ONLY " + JsonSerializer.Serialize(new
            {
                phase = "child_result", scenario, exitCode = process.ExitCode,
                passed = root.GetProperty("passed").GetBoolean(), deadlineSeconds = root.GetProperty("deadlineSeconds").GetInt32(),
                correlation = root.GetProperty("correlation").GetString(), elapsedMs = root.GetProperty("elapsedMs").GetDouble(),
                code = root.GetProperty("code").GetString(),
            }));
            return new(process.ExitCode, root.GetProperty("passed").GetBoolean(), root.GetProperty("deadlineSeconds").GetInt32(),
                root.GetProperty("code").GetString());
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true); // Only the fixture process created above.
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }

    private sealed record LifetimeResult(int ExitCode, bool Passed, int DeadlineSeconds, string? Code);
}
