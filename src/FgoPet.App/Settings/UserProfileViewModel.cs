using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FgoPet.Core.Settings;

namespace FgoPet.App.Settings;

/// <summary>
/// Edits the optional global display name. A profile name is intentionally not
/// coupled to servant address preferences, which remain servant-scoped.
/// </summary>
public sealed class UserProfileViewModel : ObservableObject
{
    private const int MaximumDisplayNameLength = 80;
    private readonly IAppSettingsStore _settings;
    private string _displayName;
    private string _statusText = "可选设置";
    private string _errorText = string.Empty;

    public UserProfileViewModel(IAppSettingsStore settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _displayName = settings.Load().UserProfile?.DisplayName?.Trim() ?? string.Empty;
        SaveCommand = new RelayCommand(Save);
        ResetCommand = new RelayCommand(Reset);
    }

    public string DisplayName
    {
        get => _displayName;
        set => SetProperty(ref _displayName, value ?? string.Empty);
    }

    public string ProfileOnlyExplanation =>
        "这是全局显示名称，不会自动成为每个角色的称呼。角色称呼仍在对应角色包的称呼设置中单独管理。";

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

    private void Save()
    {
        ErrorText = string.Empty;
        try
        {
            var normalized = NormalizeDisplayName(DisplayName);
            var current = _settings.Load();
            _settings.Save(current with
            {
                UserProfile = normalized is null ? null : new UserProfile(normalized),
            });
            DisplayName = normalized ?? string.Empty;
            StatusText = "已保存用户资料";
        }
        catch (ArgumentException error)
        {
            ErrorText = $"设置无效：{error.Message}";
            StatusText = "保存失败";
        }
        catch (Exception)
        {
            ErrorText = "设置保存失败，请稍后重试。";
            StatusText = "保存失败";
        }
    }

    private void Reset()
    {
        DisplayName = string.Empty;
        Save();
        if (string.IsNullOrEmpty(ErrorText))
        {
            StatusText = "已恢复默认用户资料";
        }
    }

    private static string? NormalizeDisplayName(string value)
    {
        if (value.Contains('\r') || value.Contains('\n'))
        {
            throw new ArgumentException("显示名称不能包含换行符。", nameof(value));
        }

        var normalized = value.Trim();
        if (normalized.Length == 0)
        {
            return null;
        }

        if (normalized.Length > MaximumDisplayNameLength)
        {
            throw new ArgumentException(
                $"显示名称不能超过 {MaximumDisplayNameLength} 个字符。",
                nameof(value));
        }

        return normalized;
    }
}
