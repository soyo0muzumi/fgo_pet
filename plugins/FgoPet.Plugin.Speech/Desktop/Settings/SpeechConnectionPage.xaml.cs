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

    private async void OnImportVoiceClick(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Filter = "WAV 音频|*.wav", CheckFileExists = true, Multiselect = false };
        if (picker.ShowDialog() == true) await _viewModel.ImportVoiceAsync(picker.FileName);
    }
    private void OnDeleteVoiceClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedVoice is not { } voice) return;
        if (MessageBox.Show(Window.GetWindow(this), $"删除音色“{voice.Name}”及本机参考副本？原始导入文件不受影响。",
            "删除音色", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) == MessageBoxResult.OK)
            _viewModel.DeleteSelectedVoice();
    }
    private void OnStopPreviewClick(object sender, RoutedEventArgs e) => _viewModel.StopPreview();

    private async void OnLoaded(object sender, RoutedEventArgs e) => await _viewModel.InitializeAsync();
}