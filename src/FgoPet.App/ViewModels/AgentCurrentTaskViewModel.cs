using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using FgoPet.App.Services;
using FgoPet.Core.Agents;
using FgoPet.Infrastructure.Agents;

namespace FgoPet.App.ViewModels;

public sealed partial class AgentCurrentTaskViewModel : ObservableObject
{
    private readonly AgentEventProjector _projector;
    private readonly AgentReconciliationService? _reconciliation;
    private readonly IAgentGateway? _gateway;
    private string? _stopRequestedIdentity;

    public AgentCurrentTaskViewModel(
        AgentEventProjector projector,
        TimeProvider time,
        AgentReconciliationService? reconciliation = null,
        IAgentGateway? gateway = null)
    {
        _projector = projector ?? throw new ArgumentNullException(nameof(projector));
        _ = time ?? throw new ArgumentNullException(nameof(time));
        _reconciliation = reconciliation;
        _gateway = gateway;
        _projector.EventApplied += OnProjectorEventApplied;
        _projector.ExecutionRestored += OnExecutionRestored;
        Refresh();
    }

    [ObservableProperty]
    private string _currentTaskId = string.Empty;

    [ObservableProperty]
    private string _currentTaskText = "暂无 Agent 任务";

    [ObservableProperty]
    private int _otherActiveCount;

    public bool HasOtherActiveTasks => OtherActiveCount > 0;

    [ObservableProperty]
    private bool _attentionRequired;

    [ObservableProperty]
    private bool _outcomeUnknown;

    [ObservableProperty]
    private string _attentionText = string.Empty;

    [ObservableProperty]
    private bool _wantsToTalk;

    [ObservableProperty]
    private bool _canRequestStop;

    [ObservableProperty]
    private bool _stopRequested;

    [ObservableProperty]
    private string _stopStatusText = string.Empty;

    public AgentTaskProjection? CurrentProjection { get; private set; }
    public event Action<AgentTaskProjection>? OpenTaskRequested;
    public event Action<AgentTaskProjection>? ArchiveRequested;

    public AgentProjectionApplyResult Apply(AgentEvent agentEvent)
    {
        ArgumentNullException.ThrowIfNull(agentEvent);
        return _projector.Apply(agentEvent);
    }

    public bool ConsumeTalkIntent()
    {
        if (!WantsToTalk)
        {
            return false;
        }

        WantsToTalk = false;
        return true;
    }

    public void OpenCurrentTask()
    {
        if (CurrentProjection is not null)
        {
            OpenTaskRequested?.Invoke(CurrentProjection);
        }
    }

    public async Task<bool> RequestStopAsync(CancellationToken cancellationToken = default)
    {
        var projection = CurrentProjection;
        if (_gateway is null || projection is null || !IsStopEligible(projection) || StopRequested)
            return false;

        AgentStopResult result;
        try
        {
            result = await _gateway.StopAsync(new AgentStopRequest(
                projection.SourceType,
                projection.SourceInstance,
                projection.TaskId,
                projection.DispatchRequestId ?? projection.TaskId), cancellationToken);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            StopStatusText = "停止未确认 · 请打开任务核对";
            return false;
        }

        switch (result.Status)
        {
            case AgentStopStatus.Requested:
            case AgentStopStatus.AlreadyRequested:
                _stopRequestedIdentity = projection.Identity;
                StopRequested = true;
                StopStatusText = "已请求停止 · 等待来源确认";
                CanRequestStop = false;
                return true;
            case AgentStopStatus.Completed:
                StopStatusText = "任务已结束 · 保留真实结果";
                CanRequestStop = false;
                return true;
            default:
                StopStatusText = "停止未确认 · 请打开任务核对";
                CanRequestStop = false;
                return false;
        }
    }

    public void RequestArchive()
    {
        if (!WantsToTalk || CurrentProjection is null)
        {
            return;
        }

        WantsToTalk = false;
        ArchiveRequested?.Invoke(CurrentProjection);
    }

    public async Task<bool> ReconcileAsync(
        AgentExecutionStatus status,
        CancellationToken cancellationToken = default)
    {
        if (_reconciliation is null || CurrentProjection is null || !OutcomeUnknown)
        {
            return false;
        }

        var result = await _reconciliation.ConfirmAsync(CurrentProjection, status, cancellationToken)
            .ConfigureAwait(false);
        if (result.Applied)
        {
            Refresh();
            return true;
        }

        AttentionText = $"待核对未更新（{result.SafeError ?? "unknown"}）";
        return false;
    }

    private void Refresh()
    {
        var active = _projector.Current
            .Where(item => item.Status is AgentExecutionStatus.Dispatching
                or AgentExecutionStatus.Active
                or AgentExecutionStatus.Attention
                or AgentExecutionStatus.DispatchOutcomeUnknown)
            .OrderByDescending(item => item.UpdatedAt)
            .ToArray();
        CurrentProjection = active.FirstOrDefault();
        CurrentTaskId = CurrentProjection?.TaskId ?? string.Empty;
        CurrentTaskText = CurrentProjection is null
            ? "暂无 Agent 任务"
            : CurrentProjection.Status == AgentExecutionStatus.DispatchOutcomeUnknown
                ? "待核对 · " + (CurrentProjection.Summary ?? CurrentProjection.TaskId)
                : CurrentProjection.Summary ?? CurrentProjection.TaskId;
        OtherActiveCount = Math.Max(0, active.Length - 1);
        OnPropertyChanged(nameof(HasOtherActiveTasks));
        var attention = active.FirstOrDefault(item => item.AttentionRequired);
        OutcomeUnknown = CurrentProjection?.Status == AgentExecutionStatus.DispatchOutcomeUnknown;
        AttentionRequired = attention is not null || OutcomeUnknown;
        AttentionText = OutcomeUnknown
            ? "待核对 · 点击打开任务；不会自动再次派发"
            : attention is null ? string.Empty : "需要你的确认 · 点击打开任务";
        var currentIdentity = CurrentProjection?.Identity;
        if (CurrentProjection is null || CurrentProjection.Status == AgentExecutionStatus.DispatchOutcomeUnknown)
        {
            _stopRequestedIdentity = null;
            StopRequested = false;
            StopStatusText = string.Empty;
        }
        else if (!string.Equals(_stopRequestedIdentity, currentIdentity, StringComparison.Ordinal))
        {
            StopRequested = false;
            StopStatusText = string.Empty;
        }

        CanRequestStop = _gateway is not null
            && CurrentProjection is not null
            && IsStopEligible(CurrentProjection)
            && !StopRequested;
        OnPropertyChanged(nameof(CurrentProjection));
    }

    private void OnProjectorEventApplied(AgentEvent agentEvent, AgentProjectionApplyResult result)
    {
        if (result is AgentProjectionApplyResult.IgnoredDuplicate or AgentProjectionApplyResult.IgnoredStale)
        {
            return;
        }

        if (agentEvent.EventType == AgentEventType.GoalCompleted)
        {
            WantsToTalk = true;
        }

        Refresh();
    }

    private void OnExecutionRestored(AgentExecution execution) => Refresh();

    private static bool IsStopEligible(AgentTaskProjection projection) =>
        projection.Status is AgentExecutionStatus.Dispatching
            or AgentExecutionStatus.Active
            or AgentExecutionStatus.Attention;
}
