using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FgoPet.UiSdk;

namespace FgoPet.Plugin.Focus.Desktop;

public partial class FocusCompactView : UserControl, ICompactSurfaceView
{
    private FocusCompactViewModel? _subscribed;
    private bool _expanded;
    private bool _disposed;
    private FocusCompactViewModel? Model => DataContext as FocusCompactViewModel;
    public FocusCompactView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach();
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
    }
    public void SetExpanded(bool expanded)
    {
        _expanded = expanded;
        ApplyState();
    }
    private void Attach()
    {
        Detach();
        if (_disposed) return;
        _subscribed = Model;
        if (_subscribed is not null) _subscribed.PropertyChanged += OnChanged;
        ApplyState();
    }
    private void Detach()
    {
        if (_subscribed is not null) _subscribed.PropertyChanged -= OnChanged;
        _subscribed = null;
    }
    public void Dispose()
    {
        _disposed = true;
        Detach();
    }
    private void OnChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(FocusCompactViewModel.ProgressPercent)) UpdateProgressArc();
        if (args.PropertyName is nameof(FocusCompactViewModel.IsActive)
            or nameof(FocusCompactViewModel.CanPause) or nameof(FocusCompactViewModel.CanStartFocus)) ApplyState();
    }
    private void ApplyState()
    {
        var active = Model?.IsActive == true;
        FocusSetupCard.Visibility = _expanded && !active ? Visibility.Visible : Visibility.Collapsed;
        CompactTimer.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        StartFocusButton.IsEnabled = Model?.CanStartFocus == true;
        UpdateProgressArc();
        var canPause = Model?.CanPause == true;
        PauseResumeButton.Content = FindResource(canPause ? "PetPauseIcon" : "PetPlayIcon");
        PauseResumeButton.ToolTip = canPause ? "暂停专注" : "继续专注";
        System.Windows.Automation.AutomationProperties.SetName(PauseResumeButton, PauseResumeButton.ToolTip.ToString());
    }
    private void OnStartFocusClick(object sender, RoutedEventArgs e) => Model?.StartFocus();

    private void OnFocusAdjustClick(object sender, RoutedEventArgs e)
    {
        if (Model is null || sender is not Button button || button.Tag is not string tag)
        {
            return;
        }

        Model.SelectCustomPreset();
        switch (tag)
        {
            case "FocusUp": Model.AdjustCustomFocus(1); break;
            case "FocusDown": Model.AdjustCustomFocus(-1); break;
            case "BreakUp": Model.AdjustCustomBreak(1); break;
            case "BreakDown": Model.AdjustCustomBreak(-1); break;
            case "CyclesUp": Model.AdjustCustomCycles(1); break;
            case "CyclesDown": Model.AdjustCustomCycles(-1); break;
        }
    }

    private void OnPauseResumeClick(object sender, RoutedEventArgs e)
    {
        if (Model?.CanPause == true)
        {
            Model.PauseTimer();
        }
        else
        {
            Model?.ResumeTimer();
        }
    }

    private void OnStopTimerClick(object sender, RoutedEventArgs e) => Model?.StopTimer();

    private void UpdateProgressArc()
    {
        FocusProgressArc.Data = BuildProgressArc(Model?.ProgressPercent ?? 0);
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
}
