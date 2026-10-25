using System.ComponentModel;
using System.Windows;
using FgoPet.UiSdk;

namespace FgoPet.App.Settings;

/// <summary>Native lifetime and routing for the single Web settings surface.</summary>
public partial class SettingsWindow : Window, IDisposable
{
    // Temporary acceptance diagnostics; enabled only by the target test's listener. Remove before release.
    private static readonly System.Diagnostics.ActivitySource OperationTiming = new("FgoPet.TestTiming.SettingsWindow");
    private readonly WebView2SurfaceHost _webSurface;
    private readonly SettingsViewModel _viewModel;
    private bool _disposed;
    private bool _syncingWebRoute;

    public SettingsWindow(SettingsViewModel viewModel, SettingsWebRootFactory webFactory)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        ArgumentNullException.ThrowIfNull(webFactory);
        using (OperationTiming.StartActivity("settings.window.initialize_xaml"))
            InitializeComponent();
        using (OperationTiming.StartActivity("settings.window.set_dimensions"))
        {
            Width = 824;
            Height = 740;
            MinWidth = 240;
            MinHeight = 240;
        }
        using (OperationTiming.StartActivity("settings.window.create_view"))
            _webSurface = webFactory.CreateView(viewModel.SelectedPageId, OnWebNavigated);
        using (OperationTiming.StartActivity("settings.window.attach_content"))
            Content = _webSurface;
        using (OperationTiming.StartActivity("settings.window.subscribe_events"))
        {
            _webSurface.Ready += OnWebReady;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            Closing += OnClosing;
        }
    }

    private void OnWebReady(object? sender, EventArgs e) => NavigateToSelectedPage();

    private void OnWebNavigated(string pageId)
    {
        if (_disposed) return;
        _syncingWebRoute = true;
        try { _viewModel.Navigate(pageId); }
        finally { _syncingWebRoute = false; }
    }

    internal void NavigateToSelectedPage() => _webSurface.PostEvent(new
    {
        type = "settings.openPage",
        pageId = _viewModel.SelectedPageId,
        itemId = _viewModel.SelectedSection == SettingsSection.RolePackages
            ? _viewModel.PackageDetail?.PackageId : null,
    });

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_syncingWebRoute || _disposed) return;
        if (e.PropertyName is nameof(SettingsViewModel.SelectedSection)
            or nameof(SettingsViewModel.SelectedPageId) or nameof(SettingsViewModel.PackageDetail))
            NavigateToSelectedPage();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_disposed || Dispatcher.HasShutdownStarted) return;
        e.Cancel = true;
        Hide();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _webSurface.Ready -= OnWebReady;
        Closing -= OnClosing;
        _webSurface.Dispose();
        Content = null;
        if (!Dispatcher.HasShutdownStarted) Close();
    }
}
