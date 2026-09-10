using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FgoPet.App.Speech;
using FgoPet.Core.Settings;
using FgoPet.Core.Speech;
using FgoPet.Core.Secrets;

namespace FgoPet.App.Settings;

public sealed record SpeechProviderChoice(SpeechProviderKind Provider, string DisplayName);

public sealed partial class SpeechConnectionViewModel : ObservableObject
{
    private const string CredentialTarget = "fgo-pet/speech/openai";
    private readonly IAppSettingsStore _settings;
    private readonly ICredentialStore _credentials;
    private readonly SpeechPlaybackCoordinator _playback;
    private string _pendingApiKey = string.Empty;

    public SpeechConnectionViewModel(
        IAppSettingsStore settings,
        ICredentialStore credentials,
        SpeechPlaybackCoordinator playback)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _playback = playback ?? throw new ArgumentNullException(nameof(playback));
        Providers =
        [
            new(SpeechProviderKind.OpenAiCompatible, "OpenAI 风格 HTTP"),
            new(SpeechProviderKind.GptSoVits, "GPT-SoVITS 本地服务"),
        ];

        var saved = _settings.Load().SpeechConnection.Normalize();
        Enabled = saved.Enabled;
        Provider = saved.Provider;
        OpenAiBaseUrl = saved.OpenAiBaseUrl;
        OpenAiModel = saved.OpenAiModel;
        OpenAiVoice = saved.OpenAiVoice;
        GptSoVitsBaseUrl = saved.GptSoVitsBaseUrl;
        GptSoVitsReferenceAudioPath = saved.GptSoVitsReferenceAudioPath;
        GptSoVitsPromptText = saved.GptSoVitsPromptText;
        GptSoVitsLanguage = saved.GptSoVitsLanguage;
        GptSoVitsPromptLanguage = saved.GptSoVitsPromptLanguage;
        AutoReadEnabled = saved.AutoReadEnabled;
        DoNotDisturb = saved.DoNotDisturb;
        AutoReadLimit = saved.AutoReadLimit;
        Rate = saved.Rate;
        Volume = saved.Volume;
        StatusText = "朗读默认关闭；保存后可在角色消息旁手动试听。";
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        PreviewCommand = new AsyncRelayCommand(PreviewAsync);
        ClearKeyCommand = new AsyncRelayCommand(ClearKeyAsync);
    }

    public IReadOnlyList<SpeechProviderChoice> Providers { get; }

    [ObservableProperty]
    private bool _enabled;

    [ObservableProperty]
    private SpeechProviderKind _provider;

    partial void OnProviderChanged(SpeechProviderKind value)
    {
        OnPropertyChanged(nameof(IsOpenAiCompatible));
        OnPropertyChanged(nameof(IsGptSoVits));
    }

    [ObservableProperty]
    private string _openAiBaseUrl = string.Empty;

    [ObservableProperty]
    private string _openAiModel = string.Empty;

    [ObservableProperty]
    private string _openAiVoice = string.Empty;

    [ObservableProperty]
    private string _gptSoVitsBaseUrl = string.Empty;

    [ObservableProperty]
    private string _gptSoVitsReferenceAudioPath = string.Empty;

    [ObservableProperty]
    private string _gptSoVitsPromptText = string.Empty;

    [ObservableProperty]
    private string _gptSoVitsLanguage = string.Empty;

    [ObservableProperty]
    private string _gptSoVitsPromptLanguage = string.Empty;

    [ObservableProperty]
    private bool _autoReadEnabled;

    [ObservableProperty]
    private bool _doNotDisturb;

    [ObservableProperty]
    private int _autoReadLimit = 300;

    [ObservableProperty]
    private double _rate = 1.0;

    [ObservableProperty]
    private double _volume = 1.0;

    [ObservableProperty]
    private bool _isKeySaved;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _errorText = string.Empty;

    public bool IsOpenAiCompatible => Provider == SpeechProviderKind.OpenAiCompatible;
    public bool IsGptSoVits => Provider == SpeechProviderKind.GptSoVits;
    public string KeyStateText => IsKeySaved
        ? "已保存密钥（存储在 Windows Credential Manager）"
        : "尚未保存密钥；本地 GPT-SoVITS 不使用云端密钥。";

    public IAsyncRelayCommand SaveCommand { get; }
    public IAsyncRelayCommand PreviewCommand { get; }
    public IAsyncRelayCommand ClearKeyCommand { get; }

    partial void OnIsKeySavedChanged(bool value) => OnPropertyChanged(nameof(KeyStateText));

    public void SetApiKey(string value) => _pendingApiKey = value?.Trim() ?? string.Empty;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        IsKeySaved = await _credentials.ExistsAsync(CredentialTarget, cancellationToken).ConfigureAwait(true);
    }

    private async Task SaveAsync()
    {
        IsBusy = true;
        ErrorText = string.Empty;
        try
        {
            await SavePendingCredentialAsync().ConfigureAwait(true);
            _settings.Save(_settings.Load() with { SpeechConnection = BuildSettings() });
            StatusText = Enabled ? "朗读设置已保存。" : "朗读设置已保存；当前保持关闭。";
        }
        catch (ArgumentException)
        {
            ErrorText = "朗读设置无效，请检查地址、语言和文本长度。";
        }
        catch (IOException)
        {
            ErrorText = "密钥保存失败，请检查 Windows Credential Manager。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task PreviewAsync()
    {
        IsBusy = true;
        ErrorText = string.Empty;
        try
        {
            await SavePendingCredentialAsync().ConfigureAwait(true);
            var draft = BuildSettings(forceEnabled: true);
            var result = await _playback.PlayAsync(
                draft.CreateRequest(),
                "这是角色朗读试听。",
                draft.Playback).ConfigureAwait(true);
            if (result.Completed)
            {
                StatusText = "试听完成。";
            }
            else
            {
                ErrorText = result.SafeError ?? "试听未完成。";
            }
        }
        catch (SpeechSynthesisException error)
        {
            ErrorText = error.Message;
        }
        catch (ArgumentException)
        {
            ErrorText = "试听设置无效，请检查当前 provider 的配置。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ClearKeyAsync()
    {
        await _credentials.DeleteAsync(CredentialTarget, CancellationToken.None).ConfigureAwait(true);
        _pendingApiKey = string.Empty;
        IsKeySaved = false;
        StatusText = "朗读云端密钥已清除。";
    }

    private async Task SavePendingCredentialAsync()
    {
        if (Provider != SpeechProviderKind.OpenAiCompatible || string.IsNullOrWhiteSpace(_pendingApiKey)) return;
        await _credentials.SaveAsync(CredentialTarget, _pendingApiKey, CancellationToken.None).ConfigureAwait(true);
        _pendingApiKey = string.Empty;
        IsKeySaved = true;
    }

    private SpeechConnectionSettings BuildSettings(bool? forceEnabled = null) => new SpeechConnectionSettings()
    {
        Enabled = forceEnabled ?? Enabled,
        Provider = Provider,
        OpenAiBaseUrl = OpenAiBaseUrl,
        OpenAiModel = OpenAiModel,
        OpenAiVoice = OpenAiVoice,
        OpenAiCredentialTarget = CredentialTarget,
        GptSoVitsBaseUrl = GptSoVitsBaseUrl,
        GptSoVitsReferenceAudioPath = GptSoVitsReferenceAudioPath,
        GptSoVitsPromptText = GptSoVitsPromptText,
        GptSoVitsLanguage = GptSoVitsLanguage,
        GptSoVitsPromptLanguage = GptSoVitsPromptLanguage,
        AutoReadEnabled = AutoReadEnabled,
        DoNotDisturb = DoNotDisturb,
        AutoReadLimit = AutoReadLimit,
        Rate = Rate,
        Volume = Volume,
    }.Normalize();
}