using System.Windows;
using System.Windows.Controls;
using FgoPet.App.ViewModels;
using FgoPet.Core.Agents;

namespace FgoPet.App.Views;

public partial class AgentCurrentTaskStrip : UserControl
{
    public AgentCurrentTaskStrip() => InitializeComponent();

    private void OnOpenTaskClick(object sender, RoutedEventArgs e) =>
        (DataContext as AgentCurrentTaskViewModel)?.OpenCurrentTask();

    private async void OnReconcileClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not AgentCurrentTaskViewModel viewModel || !viewModel.OutcomeUnknown)
        {
            return;
        }

        var choice = MessageBox.Show(
            Window.GetWindow(this),
            $"请先在 Agent 中核对任务状态。\n执行记录：{viewModel.CurrentProjection?.ExecutionId ?? "未知"}\n\n选择“是”表示已完成，选择“否”表示仍在执行；不会重新派发任务。",
            "人工核对 Agent 执行结果",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);
        var status = choice switch
        {
            MessageBoxResult.Yes => AgentExecutionStatus.Completed,
            MessageBoxResult.No => AgentExecutionStatus.Active,
            _ => (AgentExecutionStatus?)null,
        };
        if (status is not { } resolved)
        {
            return;
        }

        await viewModel.ReconcileAsync(resolved);
    }

    private void OnArchiveClick(object sender, RoutedEventArgs e) =>
        (DataContext as AgentCurrentTaskViewModel)?.RequestArchive();
}
