using System.Windows;
using System.Windows.Controls;
using FgoPet.App.Servants;

namespace FgoPet.App.Settings;

public partial class PersonalizationPage : UserControl
{
    private readonly ServantLibraryViewModel _library;
    private readonly SettingsViewModel _settings;
    private bool _loaded;

    public PersonalizationPage(PersonalizationViewModel viewModel, ServantLibraryViewModel library, SettingsViewModel settings, ThemePage? themePage = null)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = this;
        ThemeHost.Content = themePage;
        Loaded += OnLoaded;
    }

    public PersonalizationViewModel ViewModel { get; }
    public ServantLibraryViewModel Library => _library;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        if (_library.Servants.Count == 0) await _library.LoadAsync();
    }

    private void OnChangeServantClick(object sender, RoutedEventArgs e) => _settings.Select(SettingsSection.RolePackages);
}