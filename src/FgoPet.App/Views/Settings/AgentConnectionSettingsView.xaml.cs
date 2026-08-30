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

    private void OnSaveClick(object sender, RoutedEventArgs e) =>
        (DataContext as AgentConnectionSettingsViewModel)?.Save();

    private void OnClearClick(object sender, RoutedEventArgs e) =>
        (DataContext as AgentConnectionSettingsViewModel)?.ClearAgentTodoData();
}
