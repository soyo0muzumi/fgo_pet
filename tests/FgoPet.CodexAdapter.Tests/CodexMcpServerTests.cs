using FgoPet.AgentProtocol.Messages;
using FgoPet.CodexAdapter.Mcp;
using FgoPet.CodexAdapter.Relay;
using Xunit;

namespace FgoPet.CodexAdapter.Tests;

public sealed class CodexMcpServerTests
{
    [Fact]
    public async Task Mcp_exposes_only_user_confirmed_completion_tools()
    {
        var relay = new FakeRelay();
        var server = new CodexMcpServer(relay, "codex", "source-1", "task-1");

        var tools = await server.HandleAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}");
        var call = await server.HandleAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"report_task_completed\",\"arguments\":{\"user_confirmed\":true,\"summary\":\"Delivered\"}}}");

        Assert.Contains("report_task_completed", tools, StringComparison.Ordinal);
        Assert.Contains("report_goal_completed", tools, StringComparison.Ordinal);
        Assert.Contains("ok", call, StringComparison.Ordinal);
        Assert.Equal("task_completed", relay.Events.Single().EventType);
    }

    [Fact]
    public async Task Mcp_rejects_completion_without_user_confirmation()
    {
        var server = new CodexMcpServer(new FakeRelay(), "codex", "source-1", "task-1");

        var response = await server.HandleAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"report_task_completed\",\"arguments\":{\"user_confirmed\":false}}}");

        Assert.Contains("user_confirmation_required", response, StringComparison.Ordinal);
    }

    private sealed class FakeRelay : ICodexRelaySession
    {
        public List<AgentEventMessage> Events { get; } = new();
        public Task SendEventAsync(AgentEventMessage message, CancellationToken cancellationToken = default) { Events.Add(message); return Task.CompletedTask; }
    }
}
