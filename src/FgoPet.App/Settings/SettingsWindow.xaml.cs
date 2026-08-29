using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace FgoPet.App.Settings;

public delegate object? SettingsPageContentResolver(
    SettingsSection section,
    PackageDetailRoute? packageDetail);

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;
    private readonly SettingsPageContentResolver _resolvePageContent;
    private readonly Dictionary<PageCacheKey, object?> _pageCache = [];

    public SettingsWindow(
        SettingsViewModel viewModel,
        SettingsPageContentResolver resolvePageContent)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _resolvePageContent = resolvePageContent ?? throw new ArgumentNullException(nameof(resolvePageContent));
        InitializeComponent();
        DataContext = viewModel;
        SettingsNavigation.ItemsSource = viewModel.NavigationItems;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        RefreshRoute();
        Closing += OnClosing;
    }

    private void SettingsNavigation_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SettingsNavigation.SelectedValue is SettingsSection section &&
            section != _viewModel.SelectedSection)
        {
            _viewModel.Select(section);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingsViewModel.SelectedSection) or nameof(SettingsViewModel.PackageDetail))
        {
            RefreshRoute();
        }
    }

    private void RefreshRoute()
    {
        SettingsNavigation.SelectedValue = _viewModel.SelectedSection;
        PageTitleText.Text = _viewModel.PageTitle;
        PageDescriptionText.Text = _viewModel.PageDescription;
        PackageBreadcrumbText.Text = _viewModel.Breadcrumb;
        var visiblePackageRoute = _viewModel.SelectedSection == SettingsSection.RolePackages
            ? _viewModel.PackageDetail
            : null;
        // Route-keyed instances preserve page-owned, non-secret form state for this window session.
        var key = new PageCacheKey(_viewModel.SelectedSection, visiblePackageRoute);
        if (!_pageCache.TryGetValue(key, out var content))
        {
            content = _resolvePageContent(key.Section, key.PackageDetail);
            _pageCache.Add(key, content);
        }

        SettingsContent.Content = content;
        PackageBreadcrumb.Visibility = visiblePackageRoute is null
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (Dispatcher.HasShutdownStarted) return;
        e.Cancel = true;
        Hide();
    }

    private readonly record struct PageCacheKey(
        SettingsSection Section,
        PackageDetailRoute? PackageDetail);
}
