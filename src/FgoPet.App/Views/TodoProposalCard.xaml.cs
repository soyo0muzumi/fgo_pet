using System.Windows;
using System.Windows.Controls;
using FgoPet.App.ViewModels;

namespace FgoPet.App.Views;

public partial class TodoProposalCard : UserControl
{
    public static readonly RoutedEvent ViewTodoRequestedEvent = EventManager.RegisterRoutedEvent(
        "ViewTodoRequested", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(TodoProposalCard));
    public TodoProposalCard() => InitializeComponent();
    private void OnViewClick(object sender, RoutedEventArgs e) => RaiseEvent(new RoutedEventArgs(ViewTodoRequestedEvent));

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not TodoProposalViewModel proposal)
        {
            return;
        }

        try
        {
            proposal.Confirm();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.IO.IOException or UnauthorizedAccessException or System.Data.Common.DbException or KeyNotFoundException)
        {
            if (string.IsNullOrWhiteSpace(proposal.ErrorText))
            {
                proposal.ErrorText = "添加失败，请检查标题和内容，或稍后重试；草稿已保留。";
            }
        }
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        (DataContext as TodoProposalViewModel)?.Remove();
    }
}
