using System.IO;
using FgoPet.Core.Agents;
using FgoPet.Infrastructure.Agents;

namespace FgoPet.App.Services;

public sealed record AgentTaskNavigationResult(AgentOpenTaskStatus Status, string UserMessage);

public sealed class AgentTaskNavigationService
{
    private readonly IAgentGateway _gateway;
    private readonly IAgentTaskLauncher? _launcher;

    public AgentTaskNavigationService(IAgentGateway gateway, IAgentTaskLauncher? launcher = null)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _launcher = launcher;
    }

    public async Task<AgentTaskNavigationResult> OpenAsync(
        AgentExecution execution,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(execution);
        if (await TryOpenVisibleAsync(execution.SourceType, execution.RemoteTaskId, execution.TaskId, cancellationToken).ConfigureAwait(false))
        {
            return new AgentTaskNavigationResult(AgentOpenTaskStatus.Exact, "已打开 Agent 任务。");
        }

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

    public async Task<AgentTaskNavigationResult> OpenAsync(
        AgentTaskProjection projection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projection);
        if (await TryOpenVisibleAsync(projection.SourceType, projection.RemoteTaskId, projection.TaskId, cancellationToken).ConfigureAwait(false))
        {
            return new AgentTaskNavigationResult(AgentOpenTaskStatus.Exact, "已打开 Agent 任务。");
        }

        var result = await _gateway.OpenTaskAsync(
            new AgentOpenTaskRequest(projection.SourceType, projection.SourceInstance, projection.TaskId),
            cancellationToken).ConfigureAwait(false);
        var message = result.Status switch
        {
            AgentOpenTaskStatus.Exact => "已打开 Agent 任务。",
            AgentOpenTaskStatus.AppOnly => $"已打开 Agent，请确认任务 ID：{projection.TaskId}",
            AgentOpenTaskStatus.Unsupported => "当前 Agent 不支持打开任务。",
            AgentOpenTaskStatus.Offline => "Agent 当前离线，请稍后重试。",
            _ => result.SafeError ?? "无法打开 Agent 任务。",
        };
        return new AgentTaskNavigationResult(result.Status, message);
    }

    private async Task<bool> TryOpenVisibleAsync(
        string sourceType,
        string? remoteTaskId,
        string taskId,
        CancellationToken cancellationToken)
    {
        if (_launcher is null || !string.Equals(sourceType, "codex", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(remoteTaskId))
        {
            return false;
        }

        try
        {
            await _launcher.LaunchAsync(remoteTaskId, taskId, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (IOException)
        {
            // Preserve the existing Relay fallback when Codex is unavailable.
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
