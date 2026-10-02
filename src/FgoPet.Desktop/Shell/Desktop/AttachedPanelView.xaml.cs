using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FgoPet.UiSdk;
using FgoPet.App.Settings;
using FgoPet.Core.Panels;

namespace FgoPet.App.Panels;

/// <summary>Minimal desktop-pet entry shell. Detailed chat, task, and settings work stays in ordinary windows.</summary>
public partial class AttachedPanelView : UserControl, IDisposable
{
    private AttachedPanelViewModel? _model;
    private bool _disposed;

    internal bool QuickActionsExpanded { get; private set; }
    internal event Action? CompactSizeChanged;

    public AttachedPanelView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => AttachModel();
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
        if (CompactContentHost.Content is ICompactSurfaceView previous) previous.Dispose();
        if (_disposed) return;
        CompactContentHost.Content = (e.NewValue as AttachedPanelViewModel)?.CompactSurface?.CreateView();
        if (CompactContentHost.Content is not null && CompactContentHost.Content is not ICompactSurfaceView)
            throw new InvalidOperationException("Compact content must support container expansion.");
        AttachModel();
    }

    private void AttachModel()
    {
        DetachModel();
        if (_disposed) return;
        _model = DataContext as AttachedPanelViewModel;
        if (_model is not null) _model.PropertyChanged += OnModelPropertyChanged;
        ApplyState();
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DetachModel();
        if (CompactContentHost.Content is ICompactSurfaceView content) content.Dispose();
        CompactContentHost.Content = null;
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AttachedPanelViewModel.State)
            or nameof(AttachedPanelViewModel.IsCompactSurfaceActive)
            or nameof(AttachedPanelViewModel.DialogueUnreadCount)
            or nameof(AttachedPanelViewModel.HasDialogueUnread)
            or nameof(AttachedPanelViewModel.IsAutoReadEnabled))
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

        var active = _model?.IsCompactSurfaceActive == true;
        var expanded = state == AttachedPanelState.ExpandedFocus;
        CardSurface.Visibility = CompactContentHost.Content is not null && (active || expanded)
            ? Visibility.Visible : Visibility.Collapsed;
        if (CompactContentHost.Content is ICompactSurfaceView surface) surface.SetExpanded(expanded);
        FocusEntryButton.Visibility = _model?.CompactSurface is null ? Visibility.Collapsed : Visibility.Visible;
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
