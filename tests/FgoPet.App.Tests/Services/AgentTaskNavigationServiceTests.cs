using FgoPet.App.Services;
using FgoPet.Core.Agents;
using Xunit;

namespace FgoPet.App.Tests.Services;

public sealed class AgentTaskNavigationServiceTests
{
    [Theory]
    [InlineData(AgentOpenTaskStatus.Exact, "已打开")]
    [InlineData(AgentOpenTaskStatus.AppOnly, "task-1")]
    [InlineData(AgentOpenTaskStatus.Unsupported, "不支持")]
    public async Task Navigation_surfaces_capability_specific_safe_result(AgentOpenTaskStatus status, string expected)
    {
        var service = new AgentTaskNavigationService(new FakeGateway(status));
        var execution = new AgentExecution("execution-1", "todo-1", "codex", "source-1", "task-1", "dispatch-1", DateTimeOffset.UtcNow);

        var result = await service.OpenAsync(execution);

        Assert.Equal(status, result.Status);
        Assert.Contains(expected, result.UserMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("codex://", result.UserMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Navigation_opens_the_existing_remote_codex_thread_when_available()
    {
        var launcher = new FakeLauncher();
        var service = new AgentTaskNavigationService(new FakeGateway(AgentOpenTaskStatus.AppOnly), launcher);
        var execution = new AgentExecution("execution-1", "todo-1", "codex", "source-1", "task-1", "dispatch-1", DateTimeOffset.UtcNow,
            remoteTaskId: "thread-remote");

        var result = await service.OpenAsync(execution);

        Assert.Equal(AgentOpenTaskStatus.Exact, result.Status);
        Assert.Equal(("thread-remote", "task-1"), launcher.Last);
    }

    private sealed class FakeGateway(AgentOpenTaskStatus status) : IAgentGateway
    {
        public bool IsConnected => status != AgentOpenTaskStatus.Offline;
        public Task<AgentGatewayStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AgentGatewayStatus(IsConnected, "1", null, 0));
        public Task<AgentDispatchResult> DispatchAsync(AgentDispatchRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AgentOpenTaskResult> OpenTaskAsync(AgentOpenTaskRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new AgentOpenTaskResult(status));
        public Task<IReadOnlyList<AgentEvent>> QueryKnownStatesAsync(IReadOnlyList<AgentExecution> knownExecutions, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AgentEvent>>([]);
    }

    private sealed class FakeLauncher : IAgentTaskLauncher
    {
        public (string ThreadId, string TaskId)? Last { get; private set; }
        public Task LaunchAsync(string threadId, string taskId, CancellationToken cancellationToken = default)
        {
            Last = (threadId, taskId);
            return Task.CompletedTask;
        }
    }
}
