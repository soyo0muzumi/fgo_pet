using System.Windows;
using System.Windows.Controls;
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
}
