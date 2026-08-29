using System.Windows;

namespace FgoPet.App.Memory;

public partial class MemoryWindow : Window
{
    private readonly MemoryViewModel _viewModel;

    public MemoryWindow(MemoryViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = _viewModel;
    }

    public void SetActiveServant(string? servantId) => _viewModel.SetActiveServant(servantId);

    private void OnDeleteAllClick(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(
                "确定删除全部对话、记忆、称呼设置和模型连接吗？此操作不可撤销。",
                "确认删除",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) == MessageBoxResult.Yes
            && DataContext is MemoryViewModel viewModel)
        {
            viewModel.DeleteAllCommand.Execute(null);
        }
    }
}
