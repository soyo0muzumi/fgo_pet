using System.Windows;
using System.Windows.Controls;
using FgoPet.App.Servants;
using Microsoft.Win32;

namespace FgoPet.App.Settings;

public partial class RolePackagesPage : UserControl
{
    private readonly ServantLibraryViewModel _library;
    private readonly SettingsViewModel _settings;
    private bool _isLoading;

    public RolePackagesPage(ServantLibraryViewModel library, SettingsViewModel settings)
    {
        InitializeComponent();
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        DataContext = library;
        Loaded += OnLoaded;
    }

    internal async Task RefreshAsync()
    {
        if (_isLoading) return;
        _isLoading = true;
        try
        {
            await _library.LoadAsync();
        }
        finally
        {
            _isLoading = false;
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void OnOpenPackageClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ServantCardViewModel card) return;
        _library.SelectedServant = card;
        _settings.OpenPackageCommand.Execute(new PackageDetailRoute(card.PackageId, card.DisplayName));
    }

    private void OnBrowsePackageClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "FGO Pet role package (*.fgopetpack)|*.fgopetpack",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog() == true)
        {
            _library.PackFilePath = dialog.FileName;
        }
    }
}
