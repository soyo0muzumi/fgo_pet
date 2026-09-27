using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using FgoPet.Core.Backup;
using FgoPet.App.Privacy;

namespace FgoPet.App.Settings;

public partial class PrivacyPage : UserControl
{
    public const string SectionTitle = "数据与隐私";

    private readonly Memory.MemoryViewModel _viewModel;
    private readonly PrivateBackupService? _privateBackup;
    private readonly PendingBackupRestoreService? _privateBackupRestore;
    private readonly Action? _exitForRestore;

    public PrivacyPage(
        Memory.MemoryViewModel viewModel,
        PrivateBackupService? privateBackup = null,
        PendingBackupRestoreService? privateBackupRestore = null,
        Action? exitForRestore = null)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _privateBackup = privateBackup;
        _privateBackupRestore = privateBackupRestore;
        _exitForRestore = exitForRestore;
        DataContext = viewModel;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e) => await _viewModel.RefreshAsync();

    private void OnDeleteAllClick(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(
                "确定删除全部对话、记忆、称呼设置和模型连接吗？此操作不可撤销。",
                "确认删除",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) == MessageBoxResult.Yes)
        {
            _viewModel.DeleteAllCommand.Execute(null);
        }
    }

    private async void OnCreatePrivateBackupClick(object sender, RoutedEventArgs e)
    {
        if (_privateBackup is null)
        {
            PrivateBackupStatusText.Text = "私有备份服务不可用。";
            return;
        }

        var dialog = new SaveFileDialog
        {
            AddExtension = true,
            DefaultExt = BackupFormat.Extension,
            Filter = "Fgo Pet 私有备份 (*.fgopetbackup)|*.fgopetbackup",
            FileName = "fgo-pet-backup.fgopetbackup",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            await _privateBackup.CreateAsync(dialog.FileName, CancellationToken.None);
            PrivateBackupStatusText.Text = "私有备份已创建。";
        }
        catch (BackupException error)
        {
            PrivateBackupStatusText.Text = $"私有备份失败：{error.Code}";
        }
        catch (Exception)
        {
            PrivateBackupStatusText.Text = "私有备份失败：操作未完成。";
        }
    }

    private async void OnRestorePrivateBackupClick(object sender, RoutedEventArgs e)
    {
        if (_privateBackupRestore is null)
        {
            PrivateBackupStatusText.Text = "私有恢复服务不可用。";
            return;
        }

        var dialog = new OpenFileDialog
        {
            CheckFileExists = true,
            Filter = "Fgo Pet 私有备份 (*.fgopetbackup)|*.fgopetbackup",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        if (MessageBox.Show(
                "备份校验后将关闭应用，请重新打开以完成恢复。恢复会替换当前业务数据；API Key 和 Agent 凭据不会恢复，进行中的 Agent 任务需要重新核对且不会自动重新派发。确定继续吗？",
                "确认恢复私有备份",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await _privateBackupRestore.PrepareAsync(dialog.FileName, CancellationToken.None);
            PrivateBackupStatusText.Text = "备份已校验，请重新打开应用以完成恢复。";
            _exitForRestore?.Invoke();
        }
        catch (OperationCanceledException)
        {
            PrivateBackupStatusText.Text = "恢复已取消。";
        }
        catch (Exception)
        {
            PrivateBackupStatusText.Text = "恢复失败：操作未完成。";
        }
    }
}
