using System.Windows;
using System.Windows.Controls;
using FgoPet.App.ViewModels;

namespace FgoPet.App.Views;

public partial class AgentCurrentTaskStrip : UserControl
{
    public AgentCurrentTaskStrip() => InitializeComponent();

    private void OnOpenTaskClick(object sender, RoutedEventArgs e) =>
        (DataContext as AgentCurrentTaskViewModel)?.OpenCurrentTask();
}
