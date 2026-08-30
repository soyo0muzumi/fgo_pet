using System.Windows;
using System.Windows.Controls;
using FgoPet.App.ViewModels;

namespace FgoPet.App.Views.Settings;

public partial class AgentConnectionSettingsView : UserControl
{
    public AgentConnectionSettingsView(AgentConnectionSettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is AgentConnectionSettingsViewModel viewModel)
        {
            await viewModel.SaveAsync();
        }
    }

    private async void OnClearClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is AgentConnectionSettingsViewModel viewModel)
        {
            await viewModel.ClearAgentTodoDataAsync();
        }
    }
}
