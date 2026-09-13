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

    private async void OnStopClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is AgentCurrentTaskViewModel viewModel)
            await viewModel.RequestStopAsync();
    }

    private void OnReconcileClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not AgentCurrentTaskViewModel vm || !vm.OutcomeUnknown || vm.CurrentProjection is not { } projection) return;
        var menu = new ContextMenu();
        foreach (var (label, status) in new[]
        {
            ("已核实：仍在执行", AgentExecutionStatus.Active),
            ("已核实：已完成", AgentExecutionStatus.Completed),
            ("已核实：执行失败", AgentExecutionStatus.Failed),
            ("已核实：已取消", AgentExecutionStatus.Cancelled),
        })
        {
            var item = new MenuItem { Header = label };
            item.Click += async (_, _) =>
            {
                if (MessageBox.Show(Window.GetWindow(this),
                    $"执行记录：{projection.ExecutionId}\n{label}\n\n请仅在外部工具中核实后确认。此操作只更新本机记录，不会重新派发。",
                    "确认核对结果", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
                if (vm.CurrentProjection?.Identity != projection.Identity) return;
                try { await vm.ReconcileAsync(status); }
                catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or System.Data.Common.DbException or InvalidOperationException)
                { MessageBox.Show(Window.GetWindow(this), "保存核对结果失败，记录未获确认；请重新读取状态。", "核对结果"); }
            };
            menu.Items.Add(item);
        }
        menu.PlacementTarget = sender as UIElement;
        menu.IsOpen = true;
    }
    private void OnArchiveClick(object sender, RoutedEventArgs e) =>
        (DataContext as AgentCurrentTaskViewModel)?.RequestArchive();
}
