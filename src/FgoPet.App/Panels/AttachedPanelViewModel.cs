using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using FgoPet.App.Dialogue;
using FgoPet.App.Focus;
using FgoPet.Core.Bond;
using FgoPet.Core.Events;
using FgoPet.Core.Focus;
using FgoPet.Core.Panels;
using FgoPet.Core.Timeline;
using FgoPet.App.ViewModels;

namespace FgoPet.App.Panels;

/// <summary>
/// Bounded collapsible attached panel state and lists, plus Phase 2 focus/today
/// state. All time formatting happens here in App; the ViewModel consumes services,
/// never repositories, and owns no geometry. Dialogue keeps at most 20 items and
/// presents about 6; the Today list holds at most 12 rows. Startup is always
/// <see cref="AttachedPanelState.Collapsed"/>.
/// </summary>
public sealed partial class AttachedPanelViewModel : ObservableObject
{
    public const int DialogueCapacity = 20;
    public const int DialogueVisible = 6;
    public const int TodoVisibleRows = 8;
    public const int TodayCapacity = 12;

    private readonly TimeProvider _time;
    private readonly IFocusSessionService? _focus;
    private DateTimeOffset _lastInteraction;
    private bool _pointerInside;
    private TimeSpan _idleTimeout = TimeSpan.FromSeconds(30);
    private FocusStatus _lastObservedStatus = FocusStatus.Idle;

    public AttachedPanelViewModel(TimeProvider time) : this(time, focus: null, conversation: null)
    {
    }

    public AttachedPanelViewModel(
        TimeProvider time,
        IFocusSessionService? focus,
        ConversationViewModel? conversation = null,
        TodoListViewModel? todoList = null)
    {
        _time = time;
        _focus = focus;
        Conversation = conversation;
        TodoList = todoList;
        _lastInteraction = time.GetUtcNow();
        if (focus is not null)
        {
            focus.SnapshotChanged += (_, _) => OnFocusChanged();
        }
    }

    [ObservableProperty]
    private AttachedPanelState _state = AttachedPanelState.Collapsed;

    [ObservableProperty]
    private bool _autoCollapseEnabled = true;

    [ObservableProperty]
    private string _activeServantId = string.Empty;

    // Compact timer surface
    [ObservableProperty]
    private bool _isCompactTimerVisible;

    [ObservableProperty]
    private string _remainingText = string.Empty;

    [ObservableProperty]
    private string _phaseText = string.Empty;

    [ObservableProperty]
    private bool _isPaused;

    [ObservableProperty]
    private string _cycleText = string.Empty;

    [ObservableProperty]
    private string _timerMetaText = string.Empty;

    [ObservableProperty]
    private double _progressPercent;

    // Focus column: preset selection and custom integer fields
    [ObservableProperty]
    private string _selectedPresetId = "builtin.25x4";

    [ObservableProperty]
    private string _customFocusMinutesText = "25";

    [ObservableProperty]
    private string _customBreakMinutesText = "5";

    [ObservableProperty]
    private string _customCyclesText = "4";

    [ObservableProperty]
    private string _customFocusError = string.Empty;

    [ObservableProperty]
    private string _customBreakError = string.Empty;

    [ObservableProperty]
    private string _customCyclesError = string.Empty;

    // Today column
    [ObservableProperty]
    private string _bondLevelText = "-";

    [ObservableProperty]
    private string _bondRemainingText = "-";

    [ObservableProperty]
    private string _todayEffectiveText = "0";

    public ObservableCollection<DialogueItemViewModel> Dialogue { get; } = new();

    private ConversationViewModel? _conversation;

    public ConversationViewModel? Conversation
    {
        get => _conversation;
        set
        {
            if (!ReferenceEquals(_conversation, value))
            {
                _conversation = value;
                OnPropertyChanged(nameof(Conversation));
            }
        }
    }

    public ObservableCollection<TodoItemViewModel> Todo { get; } = new();

    public TodoListViewModel? TodoList { get; }

    public ObservableCollection<TimelineItemViewModel> Today { get; } = new();

    public int VisibleDialogueCount => Math.Min(Dialogue.Count, DialogueVisible);

    public int VisibleTodoCount => Math.Min(Todo.Count, TodoVisibleRows);

    public bool TodoOverflows => Todo.Count > TodoVisibleRows;

    /// <summary>True while the custom preset fields are invalid; suppresses idle collapse.</summary>
    public bool IsEditingCustomPreset =>
        !string.IsNullOrEmpty(CustomFocusError)
        || !string.IsNullOrEmpty(CustomBreakError)
        || !string.IsNullOrEmpty(CustomCyclesError);

    public bool CanStartFocus =>
        !IsEditingCustomPreset
        && Current.Session is { Status: FocusStatus.Idle or FocusStatus.Completed }
        && !string.IsNullOrEmpty(ActiveServantId);

    public bool CanPause => Current.Session.Status == FocusStatus.Focusing || Current.Session.Status == FocusStatus.Breaking;

    public bool CanResume => Current.Session.Status == FocusStatus.PausedFocus || Current.Session.Status == FocusStatus.PausedBreak;

    public bool CanStopTimer => Current.Session.Status
        is FocusStatus.Focusing or FocusStatus.Breaking or FocusStatus.PausedFocus or FocusStatus.PausedBreak;

    public string CustomTotalText => TryParseCustom(out var preset)
        ? TimeSpan.FromSeconds(preset.TotalSeconds).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)
        : "--:--:--";

    private (FocusSession Session, bool Available) Current => _focus is null
        ? (FocusSession.Idle, false)
        : (_focus.Current, true);

    public void PortraitClick()
    {
        Interact();
        State = AttachedPanelStateMachine.Transition(State, PanelAction.PortraitClick);
    }

    public void FocusClick()
    {
        Interact();
        State = AttachedPanelStateMachine.Transition(State, PanelAction.FocusClick);
    }

    public void TodayClick()
    {
        Interact();
        State = AttachedPanelStateMachine.Transition(State, PanelAction.TodayClick);
    }

    public void DialogueClick()
    {
        Interact();
        State = AttachedPanelStateMachine.Transition(State, PanelAction.DialogueClick);
    }

    public void TodoClick()
    {
        Interact();
        State = AttachedPanelStateMachine.Transition(State, PanelAction.TodoClick);
    }

    public void Escape()
    {
        Interact();
        State = AttachedPanelStateMachine.Transition(State, PanelAction.Escape);
    }

    public void SelectPreset(FocusPreset preset)
    {
        Interact();
        SelectedPresetId = preset.FocusSeconds == FocusPresetCatalog.Short.FocusSeconds
            && preset.Cycles == FocusPresetCatalog.Short.Cycles
            ? "builtin.25x4"
            : "builtin.50x2";
        CustomFocusMinutesText = (preset.FocusSeconds / 60).ToString(CultureInfo.InvariantCulture);
        CustomBreakMinutesText = (preset.BreakSeconds / 60).ToString(CultureInfo.InvariantCulture);
        CustomCyclesText = preset.Cycles.ToString(CultureInfo.InvariantCulture);
        ValidateCustomFields();
    }

    public void SelectCustomPreset()
    {
        Interact();
        SelectedPresetId = "custom";
        ValidateCustomFields();
    }

    public void AdjustCustomFocus(int direction) =>
        CustomFocusMinutesText = AdjustBounded(CustomFocusMinutesText, direction, step: 5, min: 5, max: 180);

    public void AdjustCustomBreak(int direction) =>
        CustomBreakMinutesText = AdjustBounded(CustomBreakMinutesText, direction, step: 5, min: 1, max: 60);

    public void AdjustCustomCycles(int direction) =>
        CustomCyclesText = AdjustBounded(CustomCyclesText, direction, step: 1, min: 1, max: 12);

    public void StartFocus()
    {
        Interact();
        var preset = ResolveSelectedPreset();
        if (preset is null || !CanStartFocus || _focus is null)
        {
            return;
        }

        _focus.Start(preset, ActiveServantId);
    }

    public void PauseTimer() => _focus?.Pause();

    public void ResumeTimer() => _focus?.Resume();

    public void StopTimer() => _focus?.Stop();

    public void SetActiveServant(string servantId)
    {
        ActiveServantId = servantId;
        Conversation?.SetActiveServant(servantId);
        OnPropertyChanged(nameof(CanStartFocus));
    }

    /// <summary>Refreshes the Today projection; time formatting happens here.</summary>
    public void RefreshToday(IReadOnlyList<TimelineEntry> entries)
    {
        Today.Clear();
        foreach (var entry in entries.Take(TodayCapacity))
        {
            var timeText = entry.OccurredAtUtc.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture);
            var summary = entry.Type switch
            {
                RuntimeEventType.FocusCompleted => $"完成 {FormatMinutes(entry.EffectiveSeconds)} 专注",
                RuntimeEventType.FocusStarted => "开始专注",
                RuntimeEventType.FocusStopped => "停止专注",
                RuntimeEventType.CycleCompleted => "完成一轮循环",
                RuntimeEventType.BondLevelUp => "羁绊提升",
                _ => "记录",
            };
            Today.Add(new TimelineItemViewModel(timeText, summary, entry.BondLevel?.ToString(CultureInfo.InvariantCulture)));
        }

        TodayEffectiveText = FormatMinutes(entries
            .Where(entry => entry.Type == RuntimeEventType.FocusCompleted)
            .Sum(entry => (long)entry.EffectiveSeconds));
    }

    /// <summary>Refreshes bond level text from the evaluated progress.</summary>
    public void RefreshBond(BondProgress progress)
    {
        BondLevelText = progress.Level.ToString(CultureInfo.InvariantCulture);
        BondRemainingText = progress.IsMaxLevel
            ? "已满级"
            : $"距下一级还需 {FormatMinutes(Math.Max(0, progress.NextThresholdSeconds - progress.LifetimeFocusSeconds))}";
    }

    public void AddDialogue(string text)
    {
        Interact();
        Dialogue.Add(new DialogueItemViewModel(text));
        while (Dialogue.Count > DialogueCapacity)
        {
            Dialogue.RemoveAt(0);
        }

        OnPropertyChanged(nameof(VisibleDialogueCount));
    }

    public void AddTodo(string text)
    {
        Interact();
        Todo.Add(new TodoItemViewModel(text));
        OnPropertyChanged(nameof(VisibleTodoCount));
        OnPropertyChanged(nameof(TodoOverflows));
    }

    public void PointerEntered() => _pointerInside = true;

    public void PointerLeft() => _pointerInside = false;

    /// <summary>Periodic tick that applies the 30-second idle collapse.</summary>
    public void Tick()
    {
        var next = AttachedPanelStateMachine.ApplyIdle(
            State,
            _time,
            _lastInteraction,
            _idleTimeout,
            AutoCollapseEnabled,
            !_pointerInside,
            IsEditingCustomPreset);
        if (next != State)
        {
            State = next;
        }
    }

    private void OnFocusChanged()
    {
        var (session, available) = Current;
        var active = available && session.Status
            is FocusStatus.Focusing or FocusStatus.Breaking or FocusStatus.PausedFocus or FocusStatus.PausedBreak;
        var justStarted = active && _lastObservedStatus is FocusStatus.Idle or FocusStatus.Completed;
        _lastObservedStatus = session.Status;

        IsCompactTimerVisible = active;
        IsPaused = session.Status is FocusStatus.PausedFocus or FocusStatus.PausedBreak;
        RemainingText = FormatClock(session.RemainingSeconds);
        PhaseText = session.Phase == FocusPhase.Break ? "休息" : IsPaused ? "已暂停" : "专注中";
        CycleText = session.TotalCycles > 0 ? $"第 {session.CurrentCycle} / {session.TotalCycles} 轮" : string.Empty;
        var phaseSeconds = session.Phase == FocusPhase.Break ? session.BreakSeconds : session.FocusSeconds;
        var completedSeconds = Math.Max(session.PhaseElapsedSeconds, phaseSeconds - session.RemainingSeconds);
        ProgressPercent = phaseSeconds > 0
            ? Math.Clamp(completedSeconds * 100.0 / phaseSeconds, 0, 100)
            : 0;
        TimerMetaText = phaseSeconds > 0
            ? $"本轮 {FormatClock(phaseSeconds)} · 已完成 {ProgressPercent:0}%"
            : string.Empty;

        // Starting a session from any expanded column steps down to Compact so the
        // countdown surface is immediately visible without expanding anything.
        if (justStarted && AttachedPanelStateMachine.IsExpanded(State))
        {
            State = AttachedPanelState.Compact;
        }

        OnPropertyChanged(nameof(CanStartFocus));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(CanStopTimer));
    }

    private FocusPreset? ResolveSelectedPreset()
    {
        if (SelectedPresetId == "builtin.25x4")
        {
            return FocusPresetCatalog.Short;
        }

        if (SelectedPresetId == "builtin.50x2")
        {
            return FocusPresetCatalog.Long;
        }

        return TryParseCustom(out var preset) ? preset : null;
    }

    private bool TryParseCustom(out FocusPreset preset)
    {
        preset = FocusPresetCatalog.Short;
        var focusOk = TryParseBounded(CustomFocusMinutesText, 5, 180, out var focusMinutes);
        var breakOk = TryParseBounded(CustomBreakMinutesText, 1, 60, out var breakMinutes);
        var cyclesOk = TryParseBounded(CustomCyclesText, 1, 12, out var cycles);
        if (focusOk && breakOk && cyclesOk)
        {
            try
            {
                preset = FocusPreset.Create(focusMinutes, breakMinutes, cycles);
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
        }

        return false;
    }

    private void ValidateCustomFields()
    {
        CustomFocusError = TryParseBounded(CustomFocusMinutesText, 5, 180, out _)
            ? string.Empty : "专注 5-180 分钟";
        CustomBreakError = TryParseBounded(CustomBreakMinutesText, 1, 60, out _)
            ? string.Empty : "休息 1-60 分钟";
        CustomCyclesError = TryParseBounded(CustomCyclesText, 1, 12, out _)
            ? string.Empty : "循环 1-12 次";
        OnPropertyChanged(nameof(IsEditingCustomPreset));
        OnPropertyChanged(nameof(CanStartFocus));
        OnPropertyChanged(nameof(CustomTotalText));
    }

    partial void OnCustomFocusMinutesTextChanged(string value)
    {
        if (SelectedPresetId == "custom")
        {
            ValidateCustomFields();
        }
    }

    partial void OnCustomBreakMinutesTextChanged(string value)
    {
        if (SelectedPresetId == "custom")
        {
            ValidateCustomFields();
        }
    }

    partial void OnCustomCyclesTextChanged(string value)
    {
        if (SelectedPresetId == "custom")
        {
            ValidateCustomFields();
        }
    }

    private static bool TryParseBounded(string text, int min, int max, out int value)
    {
        value = 0;
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
            && value >= min && value <= max;
    }

    private static string AdjustBounded(string text, int direction, int step, int min, int max)
    {
        var current = int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : min;
        var adjusted = Math.Clamp(current + Math.Sign(direction) * step, min, max);
        return adjusted.ToString(CultureInfo.InvariantCulture);
    }

    private static string FormatClock(int totalSeconds) =>
        $"{totalSeconds / 60:00}:{totalSeconds % 60:00}";

    private static string FormatMinutes(long totalSeconds) =>
        totalSeconds >= 3600
            ? $"{totalSeconds / 3600.0:0.#} 小时"
            : $"{totalSeconds / 60.0:0.#} 分钟";

    private void Interact() => _lastInteraction = _time.GetUtcNow();
}
