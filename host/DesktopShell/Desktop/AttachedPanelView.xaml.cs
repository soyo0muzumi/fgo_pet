using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FgoPet.App.Dialogue;
using FgoPet.App.Settings;
using FgoPet.Core.Panels;

namespace FgoPet.App.Panels;

/// <summary>Minimal desktop-pet entry shell. Detailed chat, task, and settings work stays in ordinary windows.</summary>
public partial class AttachedPanelView : UserControl
{
    private AttachedPanelViewModel? _model;

    internal bool QuickActionsExpanded { get; private set; }
    internal event Action? CompactSizeChanged;

    public AttachedPanelView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => DetachModel();
    }

    internal bool CollapseQuickActions()
    {
        if (!QuickActionsExpanded)
        {
            return false;
        }

        QuickActionsExpanded = false;
        ApplyState();
        CompactSizeChanged?.Invoke();
        return true;
    }

    internal void ApplyPhase0Clip(double width, double height, double corner)
    {
        var geometry = new RectangleGeometry(new Rect(0, 0, width, height), corner, corner);
        geometry.Freeze();
        Clip = geometry;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        DetachModel();
        _model = e.NewValue as AttachedPanelViewModel;
        if (_model is not null)
        {
            _model.PropertyChanged += OnModelPropertyChanged;
        }
        ApplyState();
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AttachedPanelViewModel.State)
            or nameof(AttachedPanelViewModel.IsCompactTimerVisible)
            or nameof(AttachedPanelViewModel.CanPause)
            or nameof(AttachedPanelViewModel.CanResume)
            or nameof(AttachedPanelViewModel.DialogueUnreadCount)
            or nameof(AttachedPanelViewModel.HasDialogueUnread)
            or nameof(AttachedPanelViewModel.IsAutoReadEnabled)
            or nameof(AttachedPanelViewModel.CanStartFocus)
            or nameof(AttachedPanelViewModel.ProgressPercent)
            or nameof(AttachedPanelViewModel.FocusDisplay))
        {
            ApplyState();
        }
    }

    private void ApplyState()
    {
        var state = _model?.State ?? AttachedPanelState.Collapsed;
        if (state == AttachedPanelState.Collapsed)
        {
            QuickActionsExpanded = false;
        }

        var timerVisible = _model?.IsCompactTimerVisible == true;
        var focusSetupVisible = state == AttachedPanelState.ExpandedFocus && !timerVisible;
        CardSurface.Visibility = timerVisible || focusSetupVisible ? Visibility.Visible : Visibility.Collapsed;
        FocusSetupCard.Visibility = focusSetupVisible ? Visibility.Visible : Visibility.Collapsed;
        CompactTimer.Visibility = timerVisible ? Visibility.Visible : Visibility.Collapsed;
        StartFocusButton.IsEnabled = _model?.CanStartFocus == true;
        CompanionControlIsland.Visibility = state == AttachedPanelState.Collapsed
            ? Visibility.Collapsed
            : Visibility.Visible;
        CompactActions.Visibility = state != AttachedPanelState.Collapsed && QuickActionsExpanded
            ? Visibility.Visible
            : Visibility.Collapsed;

        MoreEntryButton.SetResourceReference(
            ForegroundProperty,
            QuickActionsExpanded ? "Action.Primary" : "Text.Secondary");
        var moreCopy = QuickActionsExpanded ? "收起更多" : "更多";
        MoreEntryButton.ToolTip = moreCopy;
        System.Windows.Automation.AutomationProperties.SetName(MoreEntryButton, moreCopy);

        UpdateProgressArc();
        if (_model is not null)
        {
            PauseResumeButton.Content = FindResource(_model.CanPause ? "PetPauseIcon" : "PetPlayIcon");
            PauseResumeButton.ToolTip = _model.CanPause ? "暂停专注" : "继续专注";
            System.Windows.Automation.AutomationProperties.SetName(
                PauseResumeButton,
                _model.CanPause ? "暂停专注" : "继续专注");
        }
    }

    private void OnToggleQuickActions(object sender, RoutedEventArgs e)
    {
        QuickActionsExpanded = !QuickActionsExpanded;
        ApplyState();
        CompactSizeChanged?.Invoke();
    }

    private void OnDialogueClick(object sender, RoutedEventArgs e) => _model?.DialogueClick();
    private void OnSpeechClick(object sender, RoutedEventArgs e) => _model?.ToggleAutoRead();
    private void OnSettingsClick(object sender, RoutedEventArgs e) => _model?.RequestSettings(SettingsSection.Personalization);
    private void OnTasksClick(object sender, RoutedEventArgs e) => _model?.OpenTasks();

    private void OnExitClick(object sender, RoutedEventArgs e) => _model?.RequestExit();

    private void OnFocusClick(object sender, RoutedEventArgs e) => _model?.FocusClick();

    private void OnStartFocusClick(object sender, RoutedEventArgs e) => _model?.StartFocus();

    private void OnFocusAdjustClick(object sender, RoutedEventArgs e)
    {
        if (_model is null || sender is not Button button || button.Tag is not string tag)
        {
            return;
        }

        _model.SelectCustomPreset();
        switch (tag)
        {
            case "FocusUp": _model.AdjustCustomFocus(1); break;
            case "FocusDown": _model.AdjustCustomFocus(-1); break;
            case "BreakUp": _model.AdjustCustomBreak(1); break;
            case "BreakDown": _model.AdjustCustomBreak(-1); break;
            case "CyclesUp": _model.AdjustCustomCycles(1); break;
            case "CyclesDown": _model.AdjustCustomCycles(-1); break;
        }
    }

    private void OnPauseResumeClick(object sender, RoutedEventArgs e)
    {
        if (_model?.CanPause == true)
        {
            _model.PauseTimer();
        }
        else
        {
            _model?.ResumeTimer();
        }
    }

    private void OnStopTimerClick(object sender, RoutedEventArgs e) => _model?.StopTimer();

    private void UpdateProgressArc()
    {
        FocusProgressArc.Data = BuildProgressArc(_model?.ProgressPercent ?? 0);
    }

    private static Geometry BuildProgressArc(double percent)
    {
        const double size = 186;
        const double center = size / 2;
        const double radius = 89;
        var geometry = new StreamGeometry();
        var clamped = Math.Clamp(percent, 0, 100);
        if (clamped <= 0)
        {
            geometry.Freeze();
            return geometry;
        }

        var startAngle = -Math.PI / 2;
        var endAngle = startAngle + (Math.PI * 2 * clamped / 100);
        var start = new Point(center + radius * Math.Cos(startAngle), center + radius * Math.Sin(startAngle));
        var end = new Point(center + radius * Math.Cos(endAngle), center + radius * Math.Sin(endAngle));
        using (var context = geometry.Open())
        {
            context.BeginFigure(start, false, false);
            if (clamped >= 99.99)
            {
                var opposite = new Point(center - radius, center);
                context.ArcTo(opposite, new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
                context.ArcTo(start, new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
            }
            else
            {
                context.ArcTo(end, new Size(radius, radius), 0, clamped > 50, SweepDirection.Clockwise, true, false);
            }
        }

        geometry.Freeze();
        return geometry;
    }
    private void OnPointerEntered(object sender, System.Windows.Input.MouseEventArgs e) => _model?.PointerEntered();
    private void OnPointerLeft(object sender, System.Windows.Input.MouseEventArgs e) => _model?.PointerLeft();

    private void DetachModel()
    {
        if (_model is not null)
        {
            _model.PropertyChanged -= OnModelPropertyChanged;
            _model = null;
        }
    }
}
