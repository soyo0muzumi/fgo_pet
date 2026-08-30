using FgoPet.Core.Agents;

namespace FgoPet.App.Services;

public sealed record AgentTaskNavigationResult(AgentOpenTaskStatus Status, string UserMessage);

public sealed class AgentTaskNavigationService
{
    private readonly IAgentGateway _gateway;

    public AgentTaskNavigationService(IAgentGateway gateway) => _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));

    public async Task<AgentTaskNavigationResult> OpenAsync(
        AgentExecution execution,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(execution);
        var result = await _gateway.OpenTaskAsync(
            new AgentOpenTaskRequest(execution.SourceType, execution.SourceInstance, execution.TaskId),
            cancellationToken).ConfigureAwait(false);
        var message = result.Status switch
        {
            AgentOpenTaskStatus.Exact => "已打开 Agent 任务。",
            AgentOpenTaskStatus.AppOnly => $"已打开 Agent，请确认任务 ID：{execution.TaskId}",
            AgentOpenTaskStatus.Unsupported => "当前 Agent 不支持打开任务。",
            AgentOpenTaskStatus.Offline => "Agent 当前离线，请稍后重试。",
            _ => result.SafeError ?? "无法打开 Agent 任务。",
        };
        return new AgentTaskNavigationResult(result.Status, message);
    }
}
