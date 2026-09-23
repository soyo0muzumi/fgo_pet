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
        SizeChanged += (_, _) => UpdateFieldLayout();
    }

    private void UpdateFieldLayout()
    {
        var compact = ActualWidth < 500;
        Grid.SetRow(ModelField, compact ? 1 : 0);
        Grid.SetColumn(ModelField, compact ? 0 : 1);
        Grid.SetColumnSpan(ProviderField, compact ? 2 : 1);
        Grid.SetColumnSpan(ModelField, compact ? 2 : 1);
        ProviderField.Margin = new Thickness(0, 0, compact ? 0 : 16, 0);
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
