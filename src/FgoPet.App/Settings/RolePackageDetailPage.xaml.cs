using System.Windows;
using System.Windows.Controls;

namespace FgoPet.App.Settings;

public partial class RolePackageDetailPage : UserControl
{
    private readonly RolePackageDetailViewModel _viewModel;
    private bool _isLoading;

    public RolePackageDetailPage(RolePackageDetailViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = viewModel;
        Loaded += OnLoaded;
    }

    internal async Task RefreshAsync()
    {
        if (_isLoading) return;
        _isLoading = true;
        try
        {
            await _viewModel.LoadAsync();
        }
        finally
        {
            _isLoading = false;
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e) => await RefreshAsync();
    private void OnAppearanceSectionClick(object sender, RoutedEventArgs e) => _viewModel.SelectSection(RolePackageDetailSection.Appearance);
    private void OnAddressSectionClick(object sender, RoutedEventArgs e) => _viewModel.SelectSection(RolePackageDetailSection.Address);
    private void OnPackageInfoSectionClick(object sender, RoutedEventArgs e) => _viewModel.SelectSection(RolePackageDetailSection.PackageInfo);
}
