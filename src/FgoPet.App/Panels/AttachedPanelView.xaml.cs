using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FgoPet.Core.Panels;

namespace FgoPet.App.Panels;

/// <summary>Bounded, collapsible attached panel body bound to <see cref="AttachedPanelViewModel"/>.</summary>
public partial class AttachedPanelView : UserControl
{
    private AttachedPanelViewModel? _model;

    public AttachedPanelView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => DetachModel();
    }

    internal void ApplyPhase0Clip(double width, double height, double corner)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(corner, 0), true, true);
            context.LineTo(new Point(width - corner, 0), true, false);
            context.LineTo(new Point(width, corner), true, false);
            context.LineTo(new Point(width, height - corner), true, false);
            context.LineTo(new Point(width - corner, height), true, false);
            context.LineTo(new Point(corner, height), true, false);
            context.LineTo(new Point(0, height - corner), true, false);
            context.LineTo(new Point(0, corner), true, false);
        }
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
            or nameof(AttachedPanelViewModel.SelectedPresetId)
            or nameof(AttachedPanelViewModel.IsEditingCustomPreset))
        {
            ApplyState();
        }
    }

    /// <summary>
    /// Visibility switching only: never queries SQLite, advances time, computes
    /// levels, validates fields, or selects dialogue.
    /// </summary>
    private void ApplyState()
    {
        var state = _model?.State ?? AttachedPanelState.Collapsed;
        CompactActions.Visibility = state == AttachedPanelState.Collapsed ? Visibility.Collapsed : Visibility.Visible;

        // The active header column is highlighted magenta; the rest stay cyan.
        FocusButton.Foreground = AccentFor(state == AttachedPanelState.ExpandedFocus);
        TodayButton.Foreground = AccentFor(state == AttachedPanelState.ExpandedToday);
        TodoButton.Foreground = AccentFor(state == AttachedPanelState.ExpandedTodo);
        DialogueButton.Foreground = AccentFor(state == AttachedPanelState.ExpandedDialogue);

        var timerVisible = _model?.IsCompactTimerVisible == true;
        CompactMessage.Visibility = state == AttachedPanelState.Compact && !timerVisible
            ? Visibility.Visible : Visibility.Collapsed;
        CompactTimer.Visibility = state == AttachedPanelState.Compact && timerVisible
            ? Visibility.Visible : Visibility.Collapsed;

        FocusContent.Visibility = state == AttachedPanelState.ExpandedFocus ? Visibility.Visible : Visibility.Collapsed;
        TodayContent.Visibility = state == AttachedPanelState.ExpandedToday ? Visibility.Visible : Visibility.Collapsed;
        DialogueContent.Visibility = state == AttachedPanelState.ExpandedDialogue ? Visibility.Visible : Visibility.Collapsed;
        TodoContent.Visibility = state == AttachedPanelState.ExpandedTodo ? Visibility.Visible : Visibility.Collapsed;

        if (_model is not null)
        {
            PauseResumeButton.Content = _model.CanPause ? "暂停" : "继续";
            HighlightPresetButtons(_model.SelectedPresetId);
            CustomPresetFields.Visibility = _model.SelectedPresetId == "custom"
                ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void HighlightPresetButtons(string selectedPresetId)
    {
        Preset25Button.Foreground = AccentFor(selectedPresetId == "builtin.25x4");
        Preset50Button.Foreground = AccentFor(selectedPresetId == "builtin.50x2");
        CustomPresetButton.Foreground = AccentFor(selectedPresetId == "custom");
    }

    private static System.Windows.Media.Brush AccentFor(bool isActive) =>
        isActive
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD2, 0x42, 0xE8))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x70, 0xE7, 0xF5));

    private void OnFocusClick(object sender, RoutedEventArgs e) => _model?.FocusClick();
    private void OnTodayClick(object sender, RoutedEventArgs e) => _model?.TodayClick();
    private void OnDialogueClick(object sender, RoutedEventArgs e) => _model?.DialogueClick();
    private void OnTodoClick(object sender, RoutedEventArgs e) => _model?.TodoClick();
    private void OnPreset25Click(object sender, RoutedEventArgs e) => _model?.SelectPreset(Panels.FocusPresetCatalog.Short);
    private void OnPreset50Click(object sender, RoutedEventArgs e) => _model?.SelectPreset(Panels.FocusPresetCatalog.Long);
    private void OnCustomPresetClick(object sender, RoutedEventArgs e)
    {
        if (_model is null)
        {
            return;
        }

        _model.SelectCustomPreset();
        CustomPresetFields.Visibility = Visibility.Visible;
    }
    private void OnStartFocusClick(object sender, RoutedEventArgs e) => _model?.StartFocus();
    private void OnPauseResumeClick(object sender, RoutedEventArgs e)
    {
        if (_model is null)
        {
            return;
        }

        if (_model.CanPause)
        {
            _model.PauseTimer();
        }
        else
        {
            _model.ResumeTimer();
        }
    }
    private void OnStopTimerClick(object sender, RoutedEventArgs e) => _model?.StopTimer();
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
