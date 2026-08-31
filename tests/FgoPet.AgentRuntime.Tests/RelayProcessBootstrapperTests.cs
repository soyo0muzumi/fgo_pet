using System.IO.Pipes;
using FgoPet.AgentProtocol;
using FgoPet.AgentProtocol.Messages;
using FgoPet.AgentRuntime;
using FgoPet.AgentRuntime.Pipes;
using Xunit;

namespace FgoPet.AgentRuntime.Tests;

public sealed class RelayProcessBootstrapperTests
{
    [Fact]
    public async Task Bootstrap_starts_once_then_waits_for_both_pipes()
    {
        var probe = new SequenceProbe(
            new RelayProbeResult(false, null, "offline"),
            new RelayProbeResult(false, ProtocolEnvelope.CurrentProtocolVersion, "adapter offline"),
            new RelayProbeResult(true, ProtocolEnvelope.CurrentProtocolVersion, null));
        var launcher = new RecordingLauncher();

        var result = await new RelayProcessBootstrapper(probe, launcher, new ImmediateDelay())
            .EnsureReadyAsync(TestOptions(), CancellationToken.None);

        Assert.Equal(RelayBootstrapStatus.Ready, result.Status);
        Assert.Single(launcher.Starts);
        Assert.Equal(3, probe.Probes);
    }

    [Fact]
    public async Task Bootstrap_does_not_start_when_both_pipes_are_already_ready()
    {
        var probe = new SequenceProbe(new RelayProbeResult(true, ProtocolEnvelope.CurrentProtocolVersion, null));
        var launcher = new RecordingLauncher();

        var result = await new RelayProcessBootstrapper(probe, launcher, new ImmediateDelay())
            .EnsureReadyAsync(TestOptions(), CancellationToken.None);

        Assert.Equal(RelayBootstrapStatus.Ready, result.Status);
        Assert.Empty(launcher.Starts);
    }

    [Fact]
    public async Task Bootstrap_reports_a_protocol_mismatch_without_starting_or_authenticating()
    {
        var probe = new SequenceProbe(new RelayProbeResult(true, "2", null));
        var launcher = new RecordingLauncher();

        var result = await new RelayProcessBootstrapper(probe, launcher, new ImmediateDelay())
            .EnsureReadyAsync(TestOptions(), CancellationToken.None);

        Assert.Equal(RelayBootstrapStatus.VersionMismatch, result.Status);
        Assert.Empty(launcher.Starts);
        Assert.DoesNotContain("credential", probe.Requests, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Bootstrap_reports_start_failure()
    {
        var probe = new SequenceProbe(new RelayProbeResult(false, null, "offline"));
        var launcher = new RecordingLauncher(new InvalidOperationException("start failed"));

        var result = await new RelayProcessBootstrapper(probe, launcher, new ImmediateDelay())
            .EnsureReadyAsync(TestOptions(), CancellationToken.None);

        Assert.Equal(RelayBootstrapStatus.StartFailed, result.Status);
        Assert.Contains("start failed", result.Error, StringComparison.Ordinal);
        Assert.Single(launcher.Starts);
    }

    [Fact]
    public async Task Bootstrap_reports_timeout_when_pipes_never_become_ready()
    {
        var probe = new SequenceProbe(new RelayProbeResult(false, ProtocolEnvelope.CurrentProtocolVersion, "offline"));
        var launcher = new RecordingLauncher();
        var delay = new CountingDelay();

        var result = await new RelayProcessBootstrapper(probe, launcher, delay)
            .EnsureReadyAsync(TestOptions(startupTimeout: TimeSpan.FromMilliseconds(1)), CancellationToken.None);

        Assert.Equal(RelayBootstrapStatus.TimedOut, result.Status);
        Assert.Single(launcher.Starts);
        Assert.NotEmpty(delay.Delays);
    }

    [Fact]
    public async Task Json_line_client_round_trips_a_control_request_and_rejects_oversized_frames()
    {
        var pipeName = "FgoPet.AgentRuntime.Test." + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            using var reader = new StreamReader(server);
            await using var writer = new StreamWriter(server) { AutoFlush = true };
            _ = await reader.ReadLineAsync();
            await writer.WriteLineAsync("{\"result\":\"ok\"}");
        });
        var client = new JsonLinePipeClient(pipeName, TimeSpan.FromSeconds(1));

        var response = await client.SendAsync("{\"message_type\":\"connection_test\"}", CancellationToken.None);

        Assert.Equal("{\"result\":\"ok\"}", response);
        await serverTask;
    }

    private static RelayRuntimeOptions TestOptions(TimeSpan? startupTimeout = null) => new(
        "test",
        Path.Combine(Path.GetTempPath(), "FgoPet-AgentRuntime-Tests", Guid.NewGuid().ToString("N")),
        Path.Combine(AppContext.BaseDirectory, "FgoPet.AgentRelay.exe"),
        TimeSpan.FromMilliseconds(100),
        startupTimeout ?? TimeSpan.FromMilliseconds(500));

    private sealed class SequenceProbe(params RelayProbeResult[] results) : IRelayProbe
    {
        private readonly Queue<RelayProbeResult> _results = new(results);
        public int Probes { get; private set; }
        public List<string> Requests { get; } = [];

        public Task<RelayProbeResult> ProbeAsync(RelayRuntimeOptions options, CancellationToken cancellationToken)
        {
            Probes++;
            Requests.Add(options.PipeSuffix);
            return Task.FromResult(_results.Count > 1 ? _results.Dequeue() : _results.Peek());
        }
    }

    private sealed class RecordingLauncher(Exception? error = null) : IRelayProcessLauncher
    {
        public List<RelayRuntimeOptions> Starts { get; } = [];
        public void Start(RelayRuntimeOptions options)
        {
            Starts.Add(options);
            if (error is not null) throw error;
        }
    }

    private sealed class ImmediateDelay : IRuntimeDelay
    {
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class CountingDelay : IRuntimeDelay
    {
        public List<TimeSpan> Delays { get; } = [];
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            Delays.Add(delay);
            return Task.CompletedTask;
        }
    }
}
