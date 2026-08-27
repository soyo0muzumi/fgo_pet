using System.Windows;

namespace FgoPet.App.Servants;

/// <summary>Independent servant library and pack-management window.</summary>
public partial class ServantLibraryWindow : Window
{
    public ServantLibraryWindow(ServantLibraryViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        _ = viewModel.LoadAsync();
    }
}