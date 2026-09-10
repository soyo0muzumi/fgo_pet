using System.Windows;
using System.Windows.Controls;

namespace FgoPet.App.Settings;

public partial class SpeechConnectionPage : UserControl
{
    private readonly SpeechConnectionViewModel _viewModel;

    public SpeechConnectionPage(SpeechConnectionViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = viewModel;
        ApiKeyBox.PasswordChanged += (_, _) => _viewModel.SetApiKey(ApiKeyBox.Password);
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e) => await _viewModel.InitializeAsync();
}