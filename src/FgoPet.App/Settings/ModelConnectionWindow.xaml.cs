using System.Windows;

namespace FgoPet.App.Settings;

public partial class ModelConnectionWindow : Window
{
    private readonly ModelConnectionViewModel _viewModel;

    public ModelConnectionWindow(ModelConnectionViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = viewModel;
        ApiKeyBox.PasswordChanged += (_, _) => _viewModel.SetApiKey(ApiKeyBox.Password);
        Loaded += async (_, _) => await _viewModel.InitializeAsync();
        Closing += (_, e) =>
        {
            if (!Dispatcher.HasShutdownStarted)
            {
                e.Cancel = true;
                Hide();
            }
        };
    }

    private void OfflineButton_Click(object sender, RoutedEventArgs e) => Hide();
}
