using System.Windows;
using System.Windows.Controls;
using FgoPet.App.Theming;

namespace FgoPet.App.Settings;

public partial class ModelConnectionPage : UserControl
{
    private readonly ModelConnectionViewModel _viewModel;

    public ModelConnectionPage(ModelConnectionViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = viewModel;
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

    private void OnOfflineClick(object sender, RoutedEventArgs e) =>
        StatusChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Raised for the offline skip action so the host can close the shell if desired.</summary>
    public event EventHandler? StatusChanged;
}
