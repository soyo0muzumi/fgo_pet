using FgoPet.AgentProtocol.Messages;
using FgoPet.CodexAdapter.Mcp;
using FgoPet.CodexAdapter.Relay;
using Xunit;

namespace FgoPet.CodexAdapter.Tests;

public sealed class AdapterPendingMcpTests
{
    [Fact]
    public async Task Pending_pairing_does_not_block_initialize_or_tool_discovery()
    {
        var connector = new PendingConnector();
        var server = new CodexMcpServer(connector, "task-1");

        var initialized = await server.HandleAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\"}");
        var tools = await server.HandleAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}");

        Assert.Contains("protocolVersion", initialized, StringComparison.Ordinal);
        Assert.Contains("report_task_completed", tools, StringComparison.Ordinal);
        Assert.Equal(0, connector.ConnectionAttempts);
    }

    [Fact]
    public async Task Confirmed_tool_returns_approval_required_without_sending_an_event()
    {
        var connector = new PendingConnector();
        var server = new CodexMcpServer(connector, "task-1");

        var result = await server.HandleAsync("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"report_task_completed\",\"arguments\":{\"user_confirmed\":true}}}");

        Assert.Contains("approval_required", result, StringComparison.Ordinal);
        Assert.Contains("request-1", result, StringComparison.Ordinal);
        Assert.Equal(1, connector.ConnectionAttempts);
        Assert.Equal(0, connector.EventsSent);
    }

    [Fact]
    public async Task Malformed_tool_call_returns_invalid_params_instead_of_crashing_the_session()
    {
        var server = new CodexMcpServer(new PendingConnector(), "task-1");

        var result = await server.HandleAsync("{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/call\"}");

        Assert.Contains("invalid_params", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Malformed_json_does_not_prevent_the_next_initialization()
    {
        var server = new CodexMcpServer(new PendingConnector(), "task-1");
        Assert.Contains("parse_error", await server.HandleAsync("{broken"), StringComparison.Ordinal);
        Assert.Contains("serverInfo", await server.HandleAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\"}"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Initialized_notification_has_no_response()
    {
        var server = new CodexMcpServer(new PendingConnector(), "task-1");
        Assert.Empty(await server.HandleAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}"));
    }

    [Fact]
    public async Task Transport_failure_is_a_sanitized_tool_result()
    {
        var server = new CodexMcpServer(new PendingConnector { FailConnection = true }, "task-1");
        var response = await server.HandleAsync("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"report_task_completed\",\"arguments\":{\"user_confirmed\":true}}}");
        Assert.Contains("relay_offline", response, StringComparison.Ordinal);
        Assert.DoesNotContain("private-diagnostic", response, StringComparison.Ordinal);
    }

    private sealed class PendingConnector : ICodexRelayConnector
    {
        public string SourceInstanceId => "source-1";
        public int ConnectionAttempts { get; private set; }
        public int EventsSent { get; private set; }
        public bool FailConnection { get; init; }
        public bool Connected { get; init; }
        public bool OversizedResponse { get; init; }

        public Task<AdapterConnectionResult> EnsureAuthenticatedAsync(CancellationToken cancellationToken = default)
        {
            ConnectionAttempts++;
            if (FailConnection) throw new IOException("private-diagnostic");
            if (OversizedResponse) throw new InvalidDataException("private-frame-diagnostic");
            return Task.FromResult(new AdapterConnectionResult(Connected ? AdapterConnectionStatus.Connected : AdapterConnectionStatus.ApprovalRequired, "request-1"));
        }

        public Task SendEventAsync(AgentEventMessage message, CancellationToken cancellationToken = default)
        {
            EventsSent++;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<DispatchTaskRequest>> PollDispatchesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DispatchTaskRequest>>([]);
    }

    [Fact]
    public async Task Forbidden_tool_payload_is_rejected_without_leaking_it_or_ending_the_session()
    {
        var connector = new PendingConnector { Connected = true };
        var server = new CodexMcpServer(connector, "task-1");
        var response = await server.HandleAsync("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"report_task_completed\",\"arguments\":{\"user_confirmed\":true,\"summary\":\"password=private-value\"}}}");
        Assert.Contains("invalid_params", response, StringComparison.Ordinal);
        Assert.DoesNotContain("private-value", response, StringComparison.Ordinal);
        Assert.Equal(0, connector.EventsSent);
        Assert.Contains("serverInfo", await server.HandleAsync("{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"initialize\"}"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Oversized_relay_response_does_not_end_the_mcp_session()
    {
        var server = new CodexMcpServer(new PendingConnector { OversizedResponse = true }, "task-1");
        var response = await server.HandleAsync("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"report_task_completed\",\"arguments\":{\"user_confirmed\":true}}}");
        Assert.Contains("relay_offline", response, StringComparison.Ordinal);
        Assert.DoesNotContain("private-frame-diagnostic", response, StringComparison.Ordinal);
        Assert.Contains("serverInfo", await server.HandleAsync("{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"initialize\"}"), StringComparison.Ordinal);
    }
}
