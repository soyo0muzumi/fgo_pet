using System.Windows;
using System.Windows.Controls;

namespace FgoPet.App.Settings;

public partial class PrivacyPage : UserControl
{
    public const string SectionTitle = "数据与隐私";

    private readonly Memory.MemoryViewModel _viewModel;

    public PrivacyPage(Memory.MemoryViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
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
}
