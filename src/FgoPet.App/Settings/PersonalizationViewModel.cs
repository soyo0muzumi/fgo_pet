using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FgoPet.Core.Settings;

namespace FgoPet.App.Settings;

/// <summary>
/// Owns persisted portrait scale and attached-panel preferences. Theme selection
/// belongs exclusively to <see cref="ThemePage"/>.
/// </summary>
public sealed class PersonalizationViewModel : ObservableObject
{
    private static readonly IReadOnlyList<double> SupportedScaleValues = [0.50, 0.60, 0.75];

    private readonly IAppSettingsStore _settings;
    private double _scale;
    private bool _topmost;
    private bool _autoCollapseExpandedPanel;
    private bool _suppressPersistence;
    private string _statusText = "可选设置";
    private string _errorText = string.Empty;

    public PersonalizationViewModel(IAppSettingsStore settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        var saved = settings.Load();
        _scale = IsSupportedScale(saved.Scale) ? saved.Scale : AppSettings.Defaults.Scale;
        _topmost = saved.Topmost;
        _autoCollapseExpandedPanel = saved.AutoCollapseExpandedPanel;
        SaveCommand = new RelayCommand(Save);
        ResetCommand = new RelayCommand(Reset);
    }

    public IReadOnlyList<double> ScaleOptions => SupportedScaleValues;

    // Alias retained for bindings that describe the combo box as available values.
    public IReadOnlyList<double> AvailableScales => ScaleOptions;

    public double Scale
    {
        get => _scale;
        set
        {
            if (!IsSupportedScale(value))
            {
                ErrorText = "缩放仅支持 0.50、0.60 或 0.75。";
                return;
            }

            if (SetProperty(ref _scale, value))
            {
                PersistIfEnabled();
            }
        }
    }

    public bool Topmost
    {
        get => _topmost;
        set
        {
            if (SetProperty(ref _topmost, value))
            {
                PersistIfEnabled();
            }
        }
    }

    public bool AutoCollapseExpandedPanel
    {
        get => _autoCollapseExpandedPanel;
        set
        {
            if (SetProperty(ref _autoCollapseExpandedPanel, value))
            {
                PersistIfEnabled();
            }
        }
    }

    // Short alias for UI bindings while the persisted contract keeps its full name.
    public bool AutoCollapse
    {
        get => AutoCollapseExpandedPanel;
        set => AutoCollapseExpandedPanel = value;
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string ErrorText
    {
        get => _errorText;
        private set => SetProperty(ref _errorText, value);
    }

    public IRelayCommand SaveCommand { get; }

    public IRelayCommand ResetCommand { get; }

    private void Save() => Persist();

    private void Reset()
    {
        _suppressPersistence = true;
        try
        {
            Scale = AppSettings.Defaults.Scale;
            Topmost = AppSettings.Defaults.Topmost;
            AutoCollapseExpandedPanel = AppSettings.Defaults.AutoCollapseExpandedPanel;
        }
        finally
        {
            _suppressPersistence = false;
        }

        Persist("已恢复默认个性化设置");
    }

    private void PersistIfEnabled()
    {
        if (!_suppressPersistence)
        {
            Persist();
        }
    }

    private void Persist(string successText = "已保存个性化设置")
    {
        ErrorText = string.Empty;
        try
        {
            var current = _settings.Load();
            _settings.Save(current with
            {
                Scale = _scale,
                Topmost = _topmost,
                AutoCollapseExpandedPanel = _autoCollapseExpandedPanel,
            });
            StatusText = successText;
        }
        catch (Exception)
        {
            ErrorText = "个性化设置保存失败，请稍后重试。";
            StatusText = "保存失败";
        }
    }

    private static bool IsSupportedScale(double scale) => SupportedScaleValues.Contains(scale);
}
