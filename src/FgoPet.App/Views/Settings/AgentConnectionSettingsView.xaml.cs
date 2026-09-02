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

    private AgentConnectionSettingsViewModel? ViewModel => DataContext as AgentConnectionSettingsViewModel;

    private async void OnSaveClick(object sender, RoutedEventArgs e) => await RunUiOperationAsync(() => ViewModel?.SaveAsync());

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await RunUiOperationAsync(() => ViewModel?.RefreshAsync());

    private async void OnTestConnectionClick(object sender, RoutedEventArgs e) => await RunUiOperationAsync(() => ViewModel?.TestConnectionAsync());

    private async void OnClearClick(object sender, RoutedEventArgs e) => await RunUiOperationAsync(() => ViewModel?.ClearAgentTodoDataAsync());

    private async void OnArchiveClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null || !ViewModel.CanArchive || !Confirm(
                "执行 Agent 安全归档",
                "归档会在 Relay、适配器和本地数据库完成最终核对后删除符合条件的历史执行记录。删除不可恢复；未知结果不会自动重试。确定继续吗？"))
        {
            return;
        }

        await RunUiOperationAsync(() => ViewModel.RunArchiveAsync());
    }

    private async void OnApprovePendingClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AgentPendingSourceViewModel pending)
        {
            await RunUiOperationAsync(() => ViewModel?.DecideRegistrationAsync(pending.RequestId, approve: true));
        }
    }

    private async void OnRejectPendingClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AgentPendingSourceViewModel pending || !Confirm(
                "拒绝配对请求",
                $"确定拒绝“{pending.DisplayName}”的本次配对请求吗？适配器之后可以重新发起请求。"))
        {
            return;
        }

        await RunUiOperationAsync(() => ViewModel?.DecideRegistrationAsync(pending.RequestId, approve: false));
    }

    private async void OnSaveSourceClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AgentApprovedSourceViewModel source)
        {
            await RunUiOperationAsync(() => ViewModel?.SaveSourceAsync(source));
        }
    }

    private async void OnRevokeSourceClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AgentApprovedSourceViewModel source || !Confirm(
                "撤销来源授权",
                $"确定撤销“{source.DisplayName}”的授权吗？当前会话和旧凭据会立即失效。"))
        {
            return;
        }

        await RunUiOperationAsync(() => ViewModel?.RevokeSourceAsync(source));
    }

    private async Task RunUiOperationAsync(Func<Task?> operation)
    {
        try
        {
            var task = operation();
            if (task is not null) await task.ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ViewModel?.ReportUiError();
        }
    }

    private bool Confirm(string title, string message)
    {
        return MessageBox.Show(
            Window.GetWindow(this),
            message,
            title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;
    }
}
