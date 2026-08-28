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
        if (e.PropertyName == nameof(AttachedPanelViewModel.State))
        {
            ApplyState();
        }
    }

    private void ApplyState()
    {
        var state = _model?.State ?? AttachedPanelState.Collapsed;
        CompactActions.Visibility = state == AttachedPanelState.Collapsed ? Visibility.Collapsed : Visibility.Visible;
        CompactMessage.Visibility = state == AttachedPanelState.Compact ? Visibility.Visible : Visibility.Collapsed;
        DialogueContent.Visibility = state == AttachedPanelState.ExpandedDialogue ? Visibility.Visible : Visibility.Collapsed;
        TodoContent.Visibility = state == AttachedPanelState.ExpandedTodo ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnDialogueClick(object sender, RoutedEventArgs e) => _model?.DialogueClick();
    private void OnTodoClick(object sender, RoutedEventArgs e) => _model?.TodoClick();
    private void OnCollapseClick(object sender, RoutedEventArgs e) => _model?.Escape();
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
