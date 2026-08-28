using System.Windows;

namespace FgoPet.App.Servants;

/// <summary>Independent servant library and pack-management window.</summary>
public partial class ServantLibraryWindow : Window
{
    private readonly ServantLibraryViewModel _viewModel;

    public ServantLibraryWindow(ServantLibraryViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = viewModel;
        Loaded += async (_, _) => await RefreshAsync();
    }

    internal Task RefreshAsync() => _viewModel.LoadAsync();
}
