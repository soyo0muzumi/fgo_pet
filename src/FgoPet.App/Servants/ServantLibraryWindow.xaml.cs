using System.Windows;
using FgoPet.App.Memory;

namespace FgoPet.App.Servants;

/// <summary>Independent servant library and pack-management window.</summary>
public partial class ServantLibraryWindow : Window
{
    private readonly ServantLibraryViewModel _viewModel;
    private readonly MemoryWindow? _memoryWindow;

    public ServantLibraryWindow(ServantLibraryViewModel viewModel, MemoryWindow? memoryWindow = null)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _memoryWindow = memoryWindow;
        DataContext = viewModel;
        Loaded += async (_, _) => await RefreshAsync();
    }

    internal Task RefreshAsync() => _viewModel.LoadAsync();

    private void OnMemoryClick(object sender, RoutedEventArgs e)
    {
        if (_memoryWindow is null) return;
        _memoryWindow.SetActiveServant(_viewModel.SelectedServant?.ServantId);
        _memoryWindow.Show();
        _memoryWindow.Activate();
    }
}
