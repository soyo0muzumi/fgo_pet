using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FgoPet.App.Theming;
using FgoPet.Core.Settings;

namespace FgoPet.App.Settings;

/// <summary>Theme destination. Theme selection is immediate and intentionally lives here.</summary>
public partial class ThemePage : UserControl, INotifyPropertyChanged
{
    private readonly ThemeService _themeService;
    private bool _suppressChoiceEvents;

    public ThemePage(ThemeService themeService)
    {
        _themeService = themeService ?? throw new ArgumentNullException(nameof(themeService));
        InitializeComponent();
        DataContext = this;
        RefreshSelection();
        _themeService.ThemeChanged += OnThemeChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public AppTheme CurrentTheme => _themeService.CurrentTheme;

    public bool IsModernGraySelected => CurrentTheme == AppTheme.ModernGray;

    public bool IsFgoLightSelected => CurrentTheme == AppTheme.FgoLight;

    public string StatusText => _themeService.StatusText;

    public ThemeService ThemeService => _themeService;

    public void SelectTheme(AppTheme theme) => _themeService.Select(theme);

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _themeService.ThemeChanged -= OnThemeChanged;
        _themeService.ThemeChanged += OnThemeChanged;
        RefreshSelection();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => _themeService.ThemeChanged -= OnThemeChanged;

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.CheckAccess())
        {
            RefreshSelection();
        }
        else
        {
            Dispatcher.BeginInvoke(RefreshSelection, DispatcherPriority.DataBind);
        }
    }

    private void ThemeChoice_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressChoiceEvents || sender is not RadioButton choice || choice.IsChecked != true)
        {
            return;
        }

        SelectTheme(ReferenceEquals(choice, FgoLightChoice) ? AppTheme.FgoLight : AppTheme.ModernGray);
    }

    private void RefreshSelection()
    {
        _suppressChoiceEvents = true;
        try
        {
            ModernGrayChoice.IsChecked = IsModernGraySelected;
            FgoLightChoice.IsChecked = IsFgoLightSelected;
        }
        finally
        {
            _suppressChoiceEvents = false;
        }

        OnPropertyChanged(nameof(CurrentTheme));
        OnPropertyChanged(nameof(IsModernGraySelected));
        OnPropertyChanged(nameof(IsFgoLightSelected));
        OnPropertyChanged(nameof(StatusText));
    }

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
