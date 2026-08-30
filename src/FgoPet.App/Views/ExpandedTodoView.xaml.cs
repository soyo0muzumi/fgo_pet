using System.Windows;
using System.Windows.Controls;
using FgoPet.App.ViewModels;

namespace FgoPet.App.Views;

public partial class ExpandedTodoView : UserControl
{
    public ExpandedTodoView()
    {
        InitializeComponent();
        Loaded += (_, _) => (DataContext as TodoListViewModel)?.Refresh();
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
}
