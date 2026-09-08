using System.IO.Pipes;
using System.Text;
using FgoPet.AgentProtocol;
using FgoPet.AgentProtocol.Messages;
using FgoPet.AgentRuntime.Pipes;
using Xunit;

namespace FgoPet.AgentRuntime.Tests;

public sealed class RuntimeTransportRegressionTests
{
    [Fact]
    public async Task Authenticated_exchange_preserves_two_responses_received_in_one_write()
    {
        using var server = NewServer("frames-" + Guid.NewGuid().ToString("N"), out var pipeName);
        var authentication = ProtocolEnvelope.Create("auth-request", "authenticate", new { result = "authenticated" });
        var responding = RespondAsync(server, Encoding.UTF8.GetBytes(authentication.ToJson() + "\n{\"result\":\"operation_ok\"}\n"), readRequestAfterResponse: true);
        var client = new JsonLinePipeClient(pipeName, TimeSpan.FromSeconds(2));

        var response = await client.SendAuthenticatedAsync(AuthenticationRequest().ToJson(), "{\"operation\":true}");

        Assert.Equal("{\"result\":\"operation_ok\"}", response);
        await responding;
    }

    [Fact]
    public async Task Rejected_authentication_is_returned_without_sending_the_operation()
    {
        using var server = NewServer("reject-auth-" + Guid.NewGuid().ToString("N"), out var pipeName);
        var rejection = ProtocolEnvelope.Create("auth-request", "authenticate", new { result = "revoked" }).ToJson();
        var responding = Task.Run(async () =>
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await server.WaitForConnectionAsync(deadline.Token);
            using var reader = new StreamReader(server, Encoding.UTF8, false, 1024, leaveOpen: true);
            Assert.NotNull(await reader.ReadLineAsync(deadline.Token));
            await server.WriteAsync(Encoding.UTF8.GetBytes(rejection + "\n"), deadline.Token);
            Assert.Null(await reader.ReadLineAsync(deadline.Token));
        });
        var client = new JsonLinePipeClient(pipeName, TimeSpan.FromSeconds(1));

        Assert.Equal(rejection, await client.SendAuthenticatedAsync(AuthenticationRequest().ToJson(), "{\"operation\":true}"));
        await responding;
    }

    [Theory]
    [InlineData("wrong-id")]
    [InlineData("operation-first")]
    [InlineData("credential-leak")]
    public async Task Invalid_authentication_response_cannot_be_accepted_as_an_operation_result(string scenario)
    {
        using var server = NewServer("invalid-auth-" + Guid.NewGuid().ToString("N"), out var pipeName);
        var response = scenario switch
        {
            "wrong-id" => ProtocolEnvelope.Create("other-auth", "authenticate", new { result = "authenticated" }),
            "operation-first" => ProtocolEnvelope.Create("operation", "agent_event", new { result = "queued" }),
            _ => ProtocolEnvelope.Create("auth-request", "authenticate", new { result = "authenticated", credential = Convert.ToBase64String(new byte[32]) }),
        };
        var responding = Task.Run(async () =>
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await server.WaitForConnectionAsync(deadline.Token);
            using var reader = new StreamReader(server, Encoding.UTF8, false, 1024, leaveOpen: true);
            Assert.NotNull(await reader.ReadLineAsync(deadline.Token));
            await server.WriteAsync(Encoding.UTF8.GetBytes(response.ToJson() + "\n"), deadline.Token);
            Assert.Null(await reader.ReadLineAsync(deadline.Token));
        });
        var client = new JsonLinePipeClient(pipeName, TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<AgentProtocolValidationException>(() => client.SendAuthenticatedAsync(
            AuthenticationRequest().ToJson(), ProtocolEnvelope.Create("operation", "agent_event", new { }).ToJson()));
        await responding;
    }

    [Fact]
    public async Task Probe_rejects_a_connection_response_for_a_different_request()
    {
        var options = Options();
        var names = RelayPipeNames.ForCurrentUser(options);
        using var app = NewServer(names.App, out _);
        using var adapter = NewServer(names.Adapter, out _);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var responding = RespondAsync(app, ConnectionResponse("1", appOnline: false));
        var adapterConnection = adapter.WaitForConnectionAsync(deadline.Token);
        var result = await new DefaultRelayProbe().ProbeAsync(options, deadline.Token);
        Assert.False(result.Ready);
        await Task.WhenAll(responding, adapterConnection);
    }

    [Fact]
    public async Task Reader_rejects_an_oversized_frame_without_waiting_for_a_newline()
    {
        using var server = NewServer("oversized-" + Guid.NewGuid().ToString("N"), out var pipeName);
        var responding = RespondAsync(server, Encoding.UTF8.GetBytes(new string('x', 1024 * 1024 + 1)), allowDisconnect: true);
        var client = new JsonLinePipeClient(pipeName, TimeSpan.FromSeconds(2));

        await Assert.ThrowsAsync<InvalidDataException>(() => client.SendAsync("{}"));
        await responding;
    }

    [Fact]
    public async Task Reader_accepts_a_frame_at_the_byte_limit()
    {
        using var server = NewServer("limit-" + Guid.NewGuid().ToString("N"), out var pipeName);
        var expected = "\"" + new string('a', 1024 * 1024 - 2) + "\"";
        var responding = RespondAsync(server, Encoding.UTF8.GetBytes(expected + "\n"));
        var client = new JsonLinePipeClient(pipeName, TimeSpan.FromSeconds(2));

        Assert.Equal(expected, await client.SendAsync("{}"));
        await responding;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Startup_deadline_bounds_an_unresponsive_probe(bool firstProbe)
    {
        var unfinished = new TaskCompletionSource<RelayProbeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new CallbackProbe((call, _) => firstProbe || call > 1
            ? unfinished.Task
            : Task.FromResult(new RelayProbeResult(false, null, "offline")));
        var launcher = new RecordingLauncher();
        var bootstrap = new RelayProcessBootstrapper(probe, launcher, new ImmediateDelay());
        try
        {
            var result = await bootstrap.EnsureReadyAsync(Options(TimeSpan.FromMilliseconds(50)), CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(RelayBootstrapStatus.TimedOut, result.Status);
            Assert.Equal(firstProbe ? 0 : 1, launcher.Starts);
        }
        finally
        {
            unfinished.TrySetResult(new RelayProbeResult(false, null, "released"));
        }
    }

    [Fact]
    public async Task Ready_result_after_deadline_is_not_accepted()
    {
        var clock = new AdvanceTimeProvider();
        var probe = new CallbackProbe((call, _) =>
        {
            if (call == 1) return Task.FromResult(new RelayProbeResult(false, null, "offline"));
            clock.Advance(TimeSpan.FromSeconds(2));
            return Task.FromResult(new RelayProbeResult(true, "1", null));
        });
        var result = await new RelayProcessBootstrapper(probe, new RecordingLauncher(), new ImmediateDelay(), clock)
            .EnsureReadyAsync(Options(TimeSpan.FromSeconds(1)), CancellationToken.None);

        Assert.Equal(RelayBootstrapStatus.TimedOut, result.Status);
    }

    [Fact]
    public async Task Already_cancelled_bootstrap_does_not_launch_a_process()
    {
        var launcher = new RecordingLauncher();
        var probe = new CallbackProbe((_, _) => Task.FromResult(new RelayProbeResult(false, null, "offline")));
        var bootstrap = new RelayProcessBootstrapper(probe, launcher, new ImmediateDelay());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => bootstrap.EnsureReadyAsync(Options(), new CancellationToken(true)));
        Assert.Equal(0, launcher.Starts);
    }

    [Fact]
    public async Task Version_mismatch_survives_an_absent_adapter_pipe()
    {
        var options = Options();
        var names = RelayPipeNames.ForCurrentUser(options);
        using var server = NewServer(names.App, out _);
        var responding = RespondAsync(server, ConnectionResponse("2", appOnline: false), correlateProbe: true);

        var result = await new DefaultRelayProbe().ProbeAsync(options, CancellationToken.None);

        Assert.False(result.Ready);
        Assert.Equal("2", result.ProtocolVersion);
        await responding;
    }

    [Fact]
    public async Task Invalid_utf8_returns_a_sanitized_not_ready_result()
    {
        var options = Options();
        var names = RelayPipeNames.ForCurrentUser(options);
        using var server = NewServer(names.App, out _);
        var responding = RespondAsync(server, [0xff, 0x0a]);

        var result = await new DefaultRelayProbe().ProbeAsync(options, CancellationToken.None);

        Assert.False(result.Ready);
        Assert.NotNull(result.Error);
        await responding;
    }

    [Fact]
    public async Task Relay_is_ready_when_both_listeners_exist_even_with_the_app_closed()
    {
        var options = Options();
        var names = RelayPipeNames.ForCurrentUser(options);
        using var app = NewServer(names.App, out _);
        using var adapter = NewServer(names.Adapter, out _);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var responding = RespondAsync(app, ConnectionResponse("1", appOnline: false), correlateProbe: true);
        var adapterConnection = adapter.WaitForConnectionAsync(deadline.Token);

        var result = await new DefaultRelayProbe().ProbeAsync(options, deadline.Token);

        Assert.True(result.Ready);
        await Task.WhenAll(responding, adapterConnection);
    }

    private static NamedPipeServerStream NewServer(string name, out string pipeName)
    {
        pipeName = name;
        return new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }

    private static async Task RespondAsync(NamedPipeServerStream server, byte[] response, int requestCount = 1,
        bool allowDisconnect = false, bool readRequestAfterResponse = false, bool correlateProbe = false)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await server.WaitForConnectionAsync(deadline.Token);
        using var reader = new StreamReader(server, Encoding.UTF8, false, 1024, leaveOpen: true);
        for (var i = 0; i < requestCount; i++)
        {
            var request = await reader.ReadLineAsync(deadline.Token);
            Assert.NotNull(request);
            if (request.Contains("connection_test", StringComparison.Ordinal))
            {
                var envelope = ProtocolEnvelope.Parse(request);
                Assert.Equal("connection_test", envelope.MessageType);
                Assert.Equal("{}", envelope.Payload.GetRawText());
                if (correlateProbe)
                {
                    var reply = ProtocolEnvelope.Parse(Encoding.UTF8.GetString(response).TrimEnd('\n')) with { MessageId = envelope.MessageId };
                    response = Encoding.UTF8.GetBytes(reply.ToJson() + "\n");
                }
            }
        }
        try
        {
            await server.WriteAsync(response, deadline.Token);
            await server.FlushAsync(deadline.Token);
            if (readRequestAfterResponse) Assert.NotNull(await reader.ReadLineAsync(deadline.Token));
        }
        catch (IOException) when (allowDisconnect) { }
    }

    private static byte[] ConnectionResponse(string protocol, bool appOnline) => Encoding.UTF8.GetBytes(
        ProtocolEnvelope.Create("probe-response", "connection_test", new RelayConnectionTestResponse(
            true, appOnline, false, protocol, "app_offline", DateTimeOffset.UtcNow, null)).ToJson() + "\n");

    private static ProtocolEnvelope AuthenticationRequest() => ProtocolEnvelope.Create("auth-request", "authenticate",
        new AuthenticateRequest("codex", "source-1", Convert.ToBase64String(new byte[32])));

    private static RelayRuntimeOptions Options(TimeSpan? startup = null) => new(
        "regression-" + Guid.NewGuid().ToString("N"),
        Path.Combine(Path.GetTempPath(), "FgoPet-Runtime-Regression", Guid.NewGuid().ToString("N")),
        Path.Combine(AppContext.BaseDirectory, "FgoPet.AgentRelay.exe"),
        TimeSpan.FromMilliseconds(500), startup ?? TimeSpan.FromSeconds(3));

    private sealed class CallbackProbe(Func<int, CancellationToken, Task<RelayProbeResult>> callback) : IRelayProbe
    {
        private int _calls;
        public Task<RelayProbeResult> ProbeAsync(RelayRuntimeOptions options, CancellationToken cancellationToken) =>
            callback(++_calls, cancellationToken);
    }

    private sealed class RecordingLauncher : IRelayProcessLauncher
    {
        public int Starts { get; private set; }
        public void Start(RelayRuntimeOptions options) => Starts++;
    }

    private sealed class ImmediateDelay : IRuntimeDelay
    {
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class AdvanceTimeProvider : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public void Advance(TimeSpan elapsed) => _timestamp += elapsed.Ticks;
    }
}
