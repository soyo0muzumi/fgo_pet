using System.Globalization;
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
}
