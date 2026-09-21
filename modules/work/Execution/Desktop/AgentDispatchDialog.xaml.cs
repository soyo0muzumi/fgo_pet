using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FgoPet.App.ViewModels;

namespace FgoPet.App.Views;

/// <summary>
/// Visual content for the dispatch modal. The hosting Window owns the lifetime;
/// closing it cancels the view model's in-flight snapshot or dispatch request.
/// </summary>
public partial class AgentDispatchDialog : UserControl
{
    private bool _started;
    private Window? _host;

    public AgentDispatchDialog()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_started || DataContext is not AgentDispatchDialogViewModel viewModel)
        {
            return;
        }

        _started = true;
        _host = Window.GetWindow(this);
        if (_host is not null)
        {
            _host.Closed += OnHostClosed;
        }

        await viewModel.LoadAsync();
        if (_host?.IsVisible != false)
        {
            Control focusTarget = viewModel.CanSelect ? SourceCombo : CancelButton;
            FocusManager.SetFocusedElement(this, focusTarget);
            Keyboard.Focus(focusTarget);
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is AgentDispatchDialogViewModel viewModel)
        {
            viewModel.Cancel();
        }

        _host?.Close();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        e.Handled = true;
        OnCancelClick(sender, e);
    }

    private void OnHostClosed(object? sender, EventArgs e)
    {
        if (DataContext is AgentDispatchDialogViewModel viewModel)
        {
            viewModel.Cancel();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // A modal host normally raises Closed first. This also covers a visual
        // being unloaded directly by a test host or a parent teardown.
        if (_host is null)
        {
            (DataContext as AgentDispatchDialogViewModel)?.Cancel();
        }
    }
}
