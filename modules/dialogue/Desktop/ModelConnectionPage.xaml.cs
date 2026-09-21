using System.Windows;
using System.Windows.Controls;
using FgoPet.App.Theming;
using FgoPet.Core.Settings;

namespace FgoPet.App.Settings;

public partial class ModelConnectionPage : UserControl
{
    private readonly ModelConnectionViewModel _viewModel;

    public ModelConnectionPage(ModelConnectionViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = viewModel;
        _viewModel.ConnectionSaved += OnConnectionSaved;
        ApiKeyBox.PasswordChanged += (_, _) => _viewModel.SetApiKey(ApiKeyBox.Password);
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e) => await _viewModel.InitializeAsync();

    private void OnModelSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 && ModelPickerPopup.IsOpen)
        {
            _viewModel.IsModelPickerOpen = false;
        }
    }

    private void OnConnectionSaved(ModelConnectionSettings _) => StatusChanged?.Invoke(this, EventArgs.Empty);

    private void OnOfflineClick(object sender, RoutedEventArgs e) =>
        StatusChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Raised when the user saves or skips the connection so the shared host can return to Chat.</summary>
    public event EventHandler? StatusChanged;
}
