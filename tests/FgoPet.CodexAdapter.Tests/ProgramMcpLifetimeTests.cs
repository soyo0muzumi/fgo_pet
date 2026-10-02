using System.Globalization;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FgoPet.AgentProtocol.Messages;
using FgoPet.CodexAdapter.Relay;
using Xunit;

namespace FgoPet.CodexAdapter.Tests;

public sealed class ProgramMcpLifetimeTests
{
    [Fact]
    public async Task Mcp_initializes_while_bootstrap_is_pending_and_cancels_it_on_stdin_eof()
    {
        var connector = new BlockingConnector();
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\"}\n"
            + "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}\n"
            + "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}\n"));
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        await Program.RunMcpAsync(input, output, connector, "task-1", deadline.Token);

        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(2, lines.Length);
        using var initialized = JsonDocument.Parse(lines[0]);
        Assert.True(initialized.RootElement.GetProperty("result").TryGetProperty("serverInfo", out _));
        using var tools = JsonDocument.Parse(lines[1]);
        Assert.Equal(2, tools.RootElement.GetProperty("result").GetProperty("tools").GetArrayLength());
        Assert.True(connector.Started);
        Assert.True(connector.Cancelled);
    }

    [Fact]
    public async Task Mcp_stops_a_blocked_stdin_when_the_execution_worker_faults()
    {
        var connector = new BlockingConnector();
        using var input = new BlockingReadStream();
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var started = Stopwatch.StartNew();

        await Assert.ThrowsAsync<InvalidDataException>(() => Program.RunMcpAsync(
            input,
            output,
            connector,
            "task-1",
            deadline.Token,
            async token =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), token);
                throw new InvalidDataException("dispatch_journal_full");
            }));

        Assert.True(input.Cancelled.Wait(TimeSpan.FromSeconds(1)));
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(2));
    }

    private sealed class BlockingConnector : ICodexRelayConnector
    {
        public string SourceInstanceId => "source-1";
        public bool Started { get; private set; }
        public bool Cancelled { get; private set; }
        public async Task<AdapterConnectionResult> EnsureAuthenticatedAsync(CancellationToken cancellationToken = default)
        {
            Started = true;
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            finally { Cancelled = cancellationToken.IsCancellationRequested; }
            return new(AdapterConnectionStatus.ApprovalRequired);
        }
        public Task SendEventAsync(AgentEventMessage message, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<IReadOnlyList<DispatchTaskRequest>> PollDispatchesAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }

    private sealed class BlockingReadStream : Stream
    {
        public ManualResetEventSlim Cancelled { get; } = new(false);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WaitAsync(cancellationToken);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => new(WaitAsync(cancellationToken));

        private async Task<int> WaitAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 0;
            }
            catch (OperationCanceledException)
            {
                Cancelled.Set();
                throw;
            }
        }
    }
}
