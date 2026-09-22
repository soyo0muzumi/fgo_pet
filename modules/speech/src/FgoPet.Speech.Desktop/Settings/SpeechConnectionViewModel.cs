using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FgoPet.App.Speech;
using FgoPet.Core.Speech;
using FgoPet.Core.Secrets;
using FgoPet.Speech.Settings;

namespace FgoPet.App.Settings;

public sealed record SpeechProviderChoice(SpeechProviderKind Provider, string DisplayName);

public sealed partial class SpeechConnectionViewModel : ObservableObject
{
    private const string CredentialTarget = "fgo-pet/speech/openai";
    private readonly ISpeechSettingsStore _settings;
    private readonly ICredentialStore _credentials;
    private readonly SpeechPlaybackCoordinator _playback;
    private string _pendingApiKey = string.Empty;
    private readonly string _voiceDirectory;
    private bool _importing;

    public SpeechConnectionViewModel(
        ISpeechSettingsStore settings,
        ICredentialStore credentials,
        SpeechPlaybackCoordinator playback, string? voiceDirectory = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _voiceDirectory = Path.GetFullPath(voiceDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FgoPet", "voices"));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _playback = playback ?? throw new ArgumentNullException(nameof(playback));
        Providers =
        [
            new(SpeechProviderKind.IndexTts, "IndexTTS 本地音色克隆"),
            new(SpeechProviderKind.OpenAiCompatible, "OpenAI 风格 HTTP"),
            new(SpeechProviderKind.GptSoVits, "GPT-SoVITS 本地服务"),
        ];

        var saved = _settings.Load().Connection.Normalize();
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
        IndexTtsBaseUrl = saved.IndexTtsBaseUrl;
        foreach (var voice in saved.IndexTtsVoices) Voices.Add(voice);
        SelectedVoice = Voices.FirstOrDefault(v => v.Id == saved.IndexTtsVoiceId);
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
        OnPropertyChanged(nameof(IsIndexTts));
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

    [ObservableProperty] private string _indexTtsBaseUrl = "http://127.0.0.1:7860";
    [ObservableProperty] private string _voiceName = string.Empty;
    [ObservableProperty] private ReferenceVoice? _selectedVoice;
    public System.Collections.ObjectModel.ObservableCollection<ReferenceVoice> Voices { get; } = new();
    public bool IsIndexTts => Provider == SpeechProviderKind.IndexTts;

    public async Task ImportVoiceAsync(string sourcePath)
    {
        ErrorText = string.Empty;
        if (Voices.Count >= 32) { ErrorText = "音色数量已达 32 个。"; return; }
        if (string.IsNullOrWhiteSpace(VoiceName) || VoiceName.Trim().Length > 80)
        { ErrorText = "请填写 1 至 80 字的音色名称。"; return; }
        if (_importing) return;
        _importing = true;
        string? imported = null;
        try
        {
            using var source = File.OpenRead(sourcePath);
            if (source.Length is < 12 or > 20 * 1024 * 1024) throw new ArgumentException();
            var header = new byte[12];
            await source.ReadExactlyAsync(header);
            if (System.Text.Encoding.ASCII.GetString(header, 0, 4) != "RIFF" ||
                System.Text.Encoding.ASCII.GetString(header, 8, 4) != "WAVE") throw new ArgumentException();
            source.Position = 0;
            var directory = _voiceDirectory;
            Directory.CreateDirectory(directory);
            var id = Guid.NewGuid().ToString("N");
            imported = Path.Combine(directory, id + ".wav");
            await using (var target = new FileStream(imported, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await source.CopyToAsync(target);
            var voice = new ReferenceVoice(id, VoiceName.Trim(), imported);
            var previous = SelectedVoice;
            Voices.Add(voice);
            SelectedVoice = voice;
            try
            {
                var current = _settings.Load();
                _settings.Save(current with { Connection = current.Connection with
                    { IndexTtsVoices = Voices.ToArray(), IndexTtsVoiceId = voice.Id } });
            }
            catch { Voices.Remove(voice); SelectedVoice = previous; throw; }
            imported = null;
            StatusText = "音色已保存到本机；选择后可试听。参考音频仅发送给你配置的本机服务。";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { ErrorText = "音色保存失败。请使用不超过 20 MB 的 WAV，并检查本机存储权限。"; }
        finally
        {
            _importing = false;
            if (imported is not null) { try { File.Delete(imported); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
    }

    public void DeleteSelectedVoice()
    {
        if (SelectedVoice is not { } voice || _importing) return;
        ErrorText = string.Empty;
        try
        {
            var metadataOnly = voice.AudioPath.Length == 0;
            string? expected = null;
            if (!metadataOnly)
            {
                expected = Path.Combine(_voiceDirectory, voice.Id + ".wav");
                if (!Guid.TryParseExact(voice.Id, "N", out _) ||
                    !string.Equals(Path.GetFullPath(voice.AudioPath), expected, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException();
            }

            _playback.Stop();
            if (expected is not null) File.Delete(expected);
            var current = _settings.Load();
            var remaining = current.Connection.IndexTtsVoices.Where(v => v.Id != voice.Id).ToArray();
            _settings.Save(current with { Connection = current.Connection with
                { IndexTtsVoices = remaining, IndexTtsVoiceId = current.Connection.IndexTtsVoiceId == voice.Id ? "" : current.Connection.IndexTtsVoiceId } });
            Voices.Remove(voice);
            SelectedVoice = null;
            StatusText = metadataOnly
                ? "音色记录已删除；恢复备份不包含本机参考音频。"
                : "本机音色副本已删除。IndexTTS 服务自己的缓存需在该服务中管理。";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { ErrorText = "删除未完成。若文件已删除但设置未保存，可再次删除此记录；不会删除音色目录外的文件。"; }
    }
    public void StopPreview() { _playback.Stop(); StatusText = "已停止播放；本地服务可能仍在生成，迟到音频不会播放。"; }

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
            _settings.Save(_settings.Load() with { Connection = BuildSettings() });
            StatusText = Enabled ? "朗读设置已保存。" : "朗读设置已保存；当前保持关闭。";
        }
        catch (ArgumentException)
        {
            ErrorText = "朗读设置无效，请检查地址、语言和文本长度。";
        }
        catch (IOException)
        {
            ErrorText = "保存失败，请检查本机存储权限或凭据存储。";
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
        IndexTtsBaseUrl = IndexTtsBaseUrl,
        IndexTtsVoiceId = SelectedVoice?.Id ?? string.Empty,
        IndexTtsVoices = Voices.ToArray(),
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
