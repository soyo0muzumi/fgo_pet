using CommunityToolkit.Mvvm.ComponentModel;
using FgoPet.Core.Agents;
using FgoPet.Infrastructure.Agents;

namespace FgoPet.App.ViewModels;

public sealed partial class AgentCurrentTaskViewModel : ObservableObject
{
    private readonly AgentEventProjector _projector;

    public AgentCurrentTaskViewModel(AgentEventProjector projector, TimeProvider time)
    {
        _projector = projector ?? throw new ArgumentNullException(nameof(projector));
        _ = time ?? throw new ArgumentNullException(nameof(time));
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

    private void Refresh()
    {
        var active = _projector.Current
            .Where(item => item.Status is AgentExecutionStatus.Dispatching or AgentExecutionStatus.Active or AgentExecutionStatus.Attention)
            .OrderByDescending(item => item.UpdatedAt)
            .ToArray();
        CurrentProjection = active.FirstOrDefault();
        CurrentTaskId = CurrentProjection?.TaskId ?? string.Empty;
        CurrentTaskText = CurrentProjection is null
            ? "暂无 Agent 任务"
            : CurrentProjection.Summary ?? CurrentProjection.TaskId;
        OtherActiveCount = Math.Max(0, active.Length - 1);
        OnPropertyChanged(nameof(HasOtherActiveTasks));
        var attention = active.FirstOrDefault(item => item.AttentionRequired);
        AttentionRequired = attention is not null;
        AttentionText = attention is null ? string.Empty : "需要你的确认 · 点击打开任务";
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
