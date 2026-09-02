using CommunityToolkit.Mvvm.ComponentModel;
using FgoPet.App.Services;
using FgoPet.Core.Agents;
using FgoPet.Infrastructure.Agents;

namespace FgoPet.App.ViewModels;

public sealed partial class AgentCurrentTaskViewModel : ObservableObject
{
    private readonly AgentEventProjector _projector;
    private readonly AgentReconciliationService? _reconciliation;

    public AgentCurrentTaskViewModel(
        AgentEventProjector projector,
        TimeProvider time,
        AgentReconciliationService? reconciliation = null)
    {
        _projector = projector ?? throw new ArgumentNullException(nameof(projector));
        _ = time ?? throw new ArgumentNullException(nameof(time));
        _reconciliation = reconciliation;
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
}
