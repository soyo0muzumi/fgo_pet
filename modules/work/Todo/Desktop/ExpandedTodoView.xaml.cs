using System.Windows;
using System.Windows.Controls;
using System.Runtime.InteropServices;
using FgoPet.App.ViewModels;

namespace FgoPet.App.Views;

public partial class ExpandedTodoView : UserControl
{
    public ExpandedTodoView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            (DataContext as TodoListViewModel)?.Refresh();
            if (SystemParameters.ClientAreaAnimation)
            {
                TimelineRoot.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(
                    0,
                    1,
                    TimeSpan.FromMilliseconds(180)));
            }
        };
    }

    private void OnTodoTabClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is TodoListViewModel model)
        {
            model.SelectTab(TodoListTab.Todo);
        }
    }

    private void OnHistoryTabClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is TodoListViewModel model)
        {
            model.SelectTab(TodoListTab.History);
        }
    }

    private void OnDispatchClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is TodoListViewModel model
            && sender is FrameworkElement { Tag: FgoPet.Core.Todo.TodoItem todo })
        {
            model.RequestDispatch(todo);
        }
    }

    private void OnCopyDiagnosticClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string diagnostic } || string.IsNullOrWhiteSpace(diagnostic))
        {
            return;
        }

        try
        {
            Clipboard.SetText(diagnostic);
        }
        catch (ExternalException)
        {
            // Clipboard ownership is transient on Windows; a failed copy must
            // not affect the persisted execution or trigger a dispatch.
        }
    }
}
