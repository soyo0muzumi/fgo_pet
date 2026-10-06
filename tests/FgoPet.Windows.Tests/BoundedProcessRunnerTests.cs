using FgoPet.Platform.Processes;
using System.IO;
using FgoPet.Platform.Windows.Processes;
using Xunit;

namespace FgoPet.Windows.Tests;

public sealed class BoundedProcessRunnerTests
{
    [Fact]
    public async Task Command_exit_and_bounded_output_are_observed()
    {
        var runner = new WindowsBoundedProcessRunner();
        var result = await runner.RunAsync(Request("[Console]::Write('fixture'); exit 7"), default);
        Assert.True(result.Started);
        Assert.Equal(BoundedProcessExit.Completed, result.Exit);
        Assert.Equal(7, result.ExitCode);
        Assert.Equal("fixture", result.StandardOutput);
        result = await runner.RunAsync(Request("[Console]::Write(('x' * 100000))") with { MaxOutputBytes = 1024 }, default);
        Assert.True(result.Truncated);
        Assert.True(result.StandardOutput.Length <= 1024);
    }

    [Fact]
    public async Task Timeout_and_cancel_have_no_fabricated_exit_code()
    {
        var runner = new WindowsBoundedProcessRunner();
        var result = await runner.RunAsync(Request("Start-Sleep -Seconds 30") with { Timeout = TimeSpan.FromMilliseconds(200) }, default);
        Assert.True(result.Started);
        Assert.Equal(BoundedProcessExit.TimedOut, result.Exit);
        Assert.Null(result.ExitCode);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        result = await runner.RunAsync(Request("Start-Sleep -Seconds 30"), cancellation.Token);
        Assert.Equal(BoundedProcessExit.Cancelled, result.Exit);
        Assert.Null(result.ExitCode);
    }

    [Fact]
    public async Task Invalid_executable_does_not_start()
    {
        var runner = new WindowsBoundedProcessRunner();
        var result = await runner.RunAsync(Request("exit 0") with { Executable = Path.Combine(Path.GetTempPath(), "missing-fixture.exe") }, default);
        Assert.False(result.Started);
        Assert.Equal(BoundedProcessExit.LaunchFailed, result.Exit);
    }

    private static BoundedProcessRequest Request(string command) => new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
        ["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(command))],
        Path.GetTempPath(), TimeSpan.FromSeconds(10));

    [Fact]
    public async Task Suspended_process_does_not_run_when_owner_revokes_before_resume()
    {
        var checks = 0;
        var result = await new WindowsBoundedProcessRunner().RunAsync(Request("[Console]::Write('must-not-run')") with {
            BeforeStart = () => { if (++checks == 2) throw new InvalidOperationException("fixture revoked"); } }, default);
        Assert.Equal(2, checks);
        Assert.False(result.Started);
        Assert.Empty(result.StandardOutput);
    }

    [Fact]
    public async Task Timeout_terminates_normal_descendants_in_the_job()
    {
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes("Start-Sleep -Seconds 30"));
        var command = "$child = Start-Process -FilePath '" + executable + "' -ArgumentList '-NoProfile','-NonInteractive','-EncodedCommand','" + encoded + "' -WindowStyle Hidden -PassThru; [Console]::Write($child.Id); Start-Sleep -Seconds 30";
        var result = await new WindowsBoundedProcessRunner().RunAsync(Request(command) with { Timeout = TimeSpan.FromSeconds(2) }, default);
        Assert.Equal(BoundedProcessExit.TimedOut, result.Exit);
        Assert.True(int.TryParse(result.StandardOutput, out var childId));
        try { using var child = System.Diagnostics.Process.GetProcessById(childId); Assert.True(child.HasExited); }
        catch (ArgumentException) { /* The exact synthetic child is no longer in the process table. */ }
    }

    [Fact]
    public async Task Binary_stdin_is_bounded_and_diagnostics_contain_only_metadata()
    {
        var log = new Diagnostics();
        var request = Request("[Console]::OpenStandardInput().CopyTo([Console]::OpenStandardOutput())") with {
            StandardInput = System.Text.Encoding.UTF8.GetBytes("fixture-private-input"), Correlation = new string('A', 24) };
        var result = await new WindowsBoundedProcessRunner(log).RunAsync(request, default);
        Assert.Equal(BoundedProcessExit.Completed, result.Exit);
        Assert.Equal("fixture-private-input", result.StandardOutput);
        Assert.Contains(log.Items, item => item.Stage == BoundedProcessStage.Started);
        Assert.All(log.Items, item => Assert.Equal(new string('A', 24), item.Correlation));
        var serialized = System.Text.Json.JsonSerializer.Serialize(log.Items);
        Assert.DoesNotContain("fixture-private-input", serialized);
        Assert.DoesNotContain("PowerShell", serialized, StringComparison.OrdinalIgnoreCase);
    }
    private sealed class Diagnostics : IBoundedProcessDiagnostics
    {
        public List<BoundedProcessNotification> Items { get; } = [];
        public void Record(BoundedProcessNotification notification) => Items.Add(notification);
    }
    [Fact]
    public async Task Blocking_diagnostic_sink_does_not_delay_timeout_supervision()
    {
        using var release = new ManualResetEventSlim();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var result = await new WindowsBoundedProcessRunner(new BlockingDiagnostics(release)).RunAsync(
                Request("Start-Sleep -Seconds 30") with { Timeout = TimeSpan.FromMilliseconds(200) }, default);
            Assert.Equal(BoundedProcessExit.TimedOut, result.Exit);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(8));
        }
        finally { release.Set(); }
    }
    private sealed class BlockingDiagnostics(ManualResetEventSlim release) : IBoundedProcessDiagnostics
    {
        public void Record(BoundedProcessNotification notification) => release.Wait();
    }
}
