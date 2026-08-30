using System.Windows;
using System.Windows.Controls;
using FgoPet.App.ViewModels;

namespace FgoPet.App.Views;

public partial class TodoProposalCard : UserControl
{
    public TodoProposalCard() => InitializeComponent();

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
        catch (ArgumentException)
        {
            proposal.ErrorText = "标题不能为空，且内容不能超过限制。";
        }
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        (DataContext as TodoProposalViewModel)?.Remove();
    }
}
