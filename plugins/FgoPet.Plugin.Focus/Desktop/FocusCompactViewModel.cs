using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using FgoPet.App.Focus;
using FgoPet.App.Panels;
using FgoPet.App.Runtime;
using FgoPet.Core.Focus;
using FgoPet.UiSdk;

namespace FgoPet.Plugin.Focus.Desktop;

/// <summary>Owns compact Focus editing and presentation; the service remains the authoritative session.</summary>
public sealed partial class FocusCompactViewModel : ObservableObject, ICompactSurface, IDisposable
{
    private readonly IFocusSessionService? _focus;
    private readonly AppRuntime? _runtime;
    private readonly CancellationToken _stopping;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private bool _disposed;
    private bool _lastActive;
    private string _activeServantId = string.Empty;

    public FocusCompactViewModel(IFocusSessionService? focus, AppRuntime? runtime = null, CancellationToken stopping = default)
    {
        _focus = focus;
        _runtime = runtime;
        _stopping = stopping;
        _lastActive = IsActive;
        _activeServantId = runtime?.ActiveRole?.ServantId ?? string.Empty;
        if (focus is not null) focus.SnapshotChanged += OnSnapshotChanged;
        if (runtime is not null) runtime.ActiveRoleChanged += OnRoleChanged;
        RefreshSnapshot();
    }

    public bool IsActive => IsCompactTimerVisible;
    public bool BlocksAutoCollapse => IsEditingCustomPreset;
    public event Action? Interaction;
    public FrameworkElement CreateView()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _stopping.ThrowIfCancellationRequested();
        return new FocusCompactView { DataContext = this };
    }
    public string ActiveServantId => _activeServantId;
    public void SetActiveServant(string servantId)
    {
        _activeServantId = servantId;
        OnPropertyChanged(nameof(ActiveServantId));
        OnPropertyChanged(nameof(CanStartFocus));
        OnPropertyChanged(nameof(CanStartQuickFocus));
        OnPropertyChanged(nameof(QuickStartDisabledReason));
        OnPropertyChanged(nameof(StartFocusDisabledReason));
    }
    /// <summary>Compact 计时面是否可见 —— 现算自权威源，不存镜像。</summary>
    public bool IsCompactTimerVisible => IsCompactTimerSurfaceVisible(Current);

    /// <summary>计时面文案（相位 / 轮次 / 剩余时间）—— View 层类型负责格式化。</summary>
    public FocusDisplayText FocusDisplay => FocusDisplayText.From(Current.Session);

    [ObservableProperty]
    private double _progressPercent;

    // Focus column: preset selection and custom integer fields
    [ObservableProperty]
    private string _selectedPresetId = "custom";

    [ObservableProperty]
    private string _customFocusMinutesText = "25";

    [ObservableProperty]
    private string _customBreakMinutesText = "5";

    [ObservableProperty]
    private string _customCyclesText = "1";

    [ObservableProperty]
    private string _customFocusError = string.Empty;

    [ObservableProperty]
    private string _customBreakError = string.Empty;

    [ObservableProperty]
    private string _customCyclesError = string.Empty;

    /// <summary>True while the custom preset fields are invalid; suppresses idle collapse.</summary>
    public bool IsEditingCustomPreset =>
        !string.IsNullOrEmpty(CustomFocusError)
        || !string.IsNullOrEmpty(CustomBreakError)
        || !string.IsNullOrEmpty(CustomCyclesError);

    public bool CanStartQuickFocus =>
        _focus is not null && !_disposed && !_stopping.IsCancellationRequested
        && Current.Session is { Status: FocusStatus.Idle or FocusStatus.Completed }
        && !string.IsNullOrEmpty(ActiveServantId);
    public bool CanStartFocus => CanStartQuickFocus && !IsEditingCustomPreset;
    public string? QuickStartDisabledReason => CanStartQuickFocus ? null : StartFocusDisabledReason;

    public string FocusStateText => FocusStrings.Get(Current.Session.Status switch
    {
        FocusStatus.PausedFocus or FocusStatus.PausedBreak => "focus.paused",
        FocusStatus.Breaking => "focus.breaking",
        _ => "focus.running",
    });
    public string RemainingText => FocusStrings.Get("focus.remaining")
        .Replace("{time}", FormattableString.Invariant($"{Current.Session.RemainingSeconds / 60:00}:{Current.Session.RemainingSeconds % 60:00}"), StringComparison.Ordinal);
    public double RibbonRemainingPercent => 100 - ProgressPercent;
    public string PauseResumeText => FocusStrings.Get(CanPause ? "focus.pause" : "focus.resume");

    /// <summary>Why "开始专注" is disabled, or null when it is enabled. Keeps any disabled primary action explainable.</summary>
    public string? StartFocusDisabledReason
    {
        get
        {
            var session = Current.Session;
            if (session is { Status: not FocusStatus.Idle and not FocusStatus.Completed })
            {
                return FocusStrings.Get("focus.alreadyActive").Replace("{state}", FocusStateText, StringComparison.Ordinal);
            }

            if (string.IsNullOrEmpty(ActiveServantId))
            {
                return FocusStrings.Get("focus.chooseCharacter");
            }

            if (IsEditingCustomPreset)
            {
                return FocusStrings.Get("focus.invalidOptions");
            }

            return _focus is null ? FocusStrings.Get("focus.unavailable") : null;
        }
    }

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

    /// <summary>
    /// Compact 计时面的可见性判据：会话处于四个"进行中"状态之一。
    /// 权威源在 focus，shell 侧只是读它 —— 不另存一份。
    /// </summary>
    private static bool IsCompactTimerSurfaceVisible(FocusStatus status) =>
        status is FocusStatus.Focusing
            or FocusStatus.Breaking
            or FocusStatus.PausedFocus
            or FocusStatus.PausedBreak;

    private static bool IsCompactTimerSurfaceVisible((FocusSession Session, bool Available) current) =>
        current.Available && IsCompactTimerSurfaceVisible(current.Session.Status);

    public void SelectPreset(FocusPreset preset)
    {
        Interaction?.Invoke();
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
        Interaction?.Invoke();
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
        Interaction?.Invoke();
        var preset = ResolveSelectedPreset();
        if (preset is null || !CanStartFocus || _focus is null)
        {
            return;
        }

        _focus.Start(preset, ActiveServantId);
    }

    public void StartQuickFocus(int minutes)
    {
        if (minutes is not (10 or 25 or 50) || !CanOperate || IsActive) return;
        SelectedPresetId = "custom";
        CustomFocusMinutesText = minutes.ToString(CultureInfo.InvariantCulture);
        CustomBreakMinutesText = "5";
        CustomCyclesText = "1";
        ValidateCustomFields();
        StartFocus();
    }

    public void PauseTimer() { if (CanOperate) _focus?.Pause(); }

    public void ResumeTimer() { if (CanOperate) _focus?.Resume(); }

    public void StopTimer() { if (CanOperate) _focus?.Stop(); }
    private bool CanOperate => !_disposed && !_stopping.IsCancellationRequested;


    private void OnSnapshotChanged(object? sender, EventArgs args) => Dispatch(RefreshSnapshot);
    private void OnRoleChanged(object? sender, AppStateChangedEventArgs<ActiveRoleState> args) =>
        Dispatch(() => SetActiveServant(_runtime?.ActiveRole?.ServantId ?? string.Empty));
    private void Dispatch(Action action)
    {
        if (_disposed || _stopping.IsCancellationRequested || _dispatcher.HasShutdownStarted) return;
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.BeginInvoke(() =>
        {
            if (!_disposed && !_stopping.IsCancellationRequested) action();
        });
    }
    private void RefreshSnapshot()
    {
        var session = Current.Session;
        var active = IsActive;
        if (_lastActive != active)
        {
            _lastActive = active;
            OnPropertyChanged(nameof(IsActive));
            OnPropertyChanged(nameof(IsCompactTimerVisible));
        }
        OnPropertyChanged(nameof(FocusDisplay));
        var phaseSeconds = session.Phase == FocusPhase.Break ? session.BreakSeconds : session.FocusSeconds;
        var completedSeconds = Math.Max(session.PhaseElapsedSeconds, phaseSeconds - session.RemainingSeconds);
        ProgressPercent = phaseSeconds > 0 ? Math.Clamp(completedSeconds * 100.0 / phaseSeconds, 0, 100) : 0;
        OnPropertyChanged(nameof(RibbonRemainingPercent));
        OnPropertyChanged(nameof(FocusStateText));
        OnPropertyChanged(nameof(RemainingText));
        OnPropertyChanged(nameof(PauseResumeText));
        OnPropertyChanged(nameof(CanStartFocus));
        OnPropertyChanged(nameof(CanStartQuickFocus));
        OnPropertyChanged(nameof(QuickStartDisabledReason));
        OnPropertyChanged(nameof(StartFocusDisabledReason));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(CanStopTimer));
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_focus is not null) _focus.SnapshotChanged -= OnSnapshotChanged;
        if (_runtime is not null) _runtime.ActiveRoleChanged -= OnRoleChanged;
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
            ? string.Empty : FocusStrings.Get("focus.durationRange");
        CustomBreakError = TryParseBounded(CustomBreakMinutesText, 1, 60, out _)
            ? string.Empty : FocusStrings.Get("focus.breakRange");
        CustomCyclesError = TryParseBounded(CustomCyclesText, 1, 12, out _)
            ? string.Empty : FocusStrings.Get("focus.cyclesRange");
        OnPropertyChanged(nameof(IsEditingCustomPreset));
        OnPropertyChanged(nameof(BlocksAutoCollapse));
        OnPropertyChanged(nameof(CanStartFocus));
        OnPropertyChanged(nameof(StartFocusDisabledReason));
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

}
