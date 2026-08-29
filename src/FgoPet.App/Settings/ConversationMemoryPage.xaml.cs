using System.Windows;
using System.Windows.Controls;

namespace FgoPet.App.Settings;

public partial class ConversationMemoryPage : UserControl
{
    public const string SectionTitle = "对话与记忆";

    private readonly Memory.MemoryViewModel _viewModel;

    public ConversationMemoryPage(Memory.MemoryViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = viewModel;
        Loaded += OnLoaded;
    }

    public event EventHandler? DeleteAllRequested;

    /// <summary>Raises the destructive-confirmation prompt; the host decides the actual dialog.</summary>
    public void RequestDeleteAll() => DeleteAllRequested?.Invoke(this, EventArgs.Empty);

    private async void OnLoaded(object sender, RoutedEventArgs e) => await _viewModel.RefreshAsync();

    private void OnDeleteAllClick(object sender, RoutedEventArgs e) => RequestDeleteAll();
}
