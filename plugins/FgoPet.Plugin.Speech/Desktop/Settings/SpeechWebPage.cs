using System.Text.Json;
using FgoPet.App.Speech;
using FgoPet.Core.Secrets;
using FgoPet.Core.Speech;
using FgoPet.Speech.Settings;
using FgoPet.UiSdk;

namespace FgoPet.App.Settings;

/// <summary>Owns the Web command adapter for the existing speech settings page.</summary>
/// <remarks>
/// Reuses the DI singleton <see cref="SpeechConnectionViewModel"/> so the draft survives page
/// navigation exactly like the WPF page. Each Web session gets an independent adapter that shares
/// that same singleton view model (see <see cref="CreateSession"/>). The host-native voice picker
/// is an injected abstraction so the Web surface never receives a file path and tests can substitute
/// a synthetic source.
/// </remarks>
public sealed class SpeechWebPage : ISettingsWebPage
{
    /// <summary>Maximum number of reference voices the UI and VM accept.</summary>
    internal const int VoiceLimit = 32;

    private static readonly string[] PageCommands =
    [
        "speech.get",
        "speech.setDraft",
        "speech.selectVoice",
        "speech.save",
        "speech.preview",
        "speech.stopPreview",
        "speech.clearKey",
        "speech.importVoice",
        "speech.deleteVoice",
    ];

    private readonly SpeechConnectionViewModel _viewModel;
    private readonly ISpeechVoiceFilePicker _picker;
    private readonly object _initGate = new();
    private Task? _initTask;

    public SpeechWebPage(SpeechConnectionViewModel viewModel, ISpeechVoiceFilePicker picker)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _picker = picker ?? throw new ArgumentNullException(nameof(picker));
    }

    public string SettingsPageId => "Speech";

    public string ModulePath => "pages/speech.js";

    public IReadOnlyList<string> Commands => PageCommands;

    public ISettingsWebPage CreateSession() => new SpeechWebPage(_viewModel, _picker);

    public async ValueTask<WebSurfaceCommandResult> HandleCommandAsync(WebSurfaceMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryReadPageId(message.Payload, out var pageId)
                || !string.Equals(pageId, SettingsPageId, StringComparison.Ordinal))
                return Failure("SETTINGS_INVALID_INPUT");

            return message.Type switch
            {
                "speech.get" => await GetAsync(message.Payload, cancellationToken).ConfigureAwait(true),
                "speech.setDraft" => SetDraft(message.Payload, cancellationToken),
                "speech.selectVoice" => SelectVoice(message.Payload, cancellationToken),
                "speech.save" => await SaveAsync(message.Payload, cancellationToken).ConfigureAwait(true),
                "speech.preview" => await PreviewAsync(message.Payload, cancellationToken).ConfigureAwait(true),
                "speech.stopPreview" => StopPreview(message.Payload, cancellationToken),
                "speech.clearKey" => await ClearKeyAsync(message.Payload, cancellationToken).ConfigureAwait(true),
                "speech.importVoice" => await ImportVoiceAsync(message.Payload, cancellationToken).ConfigureAwait(true),
                "speech.deleteVoice" => DeleteVoice(message.Payload, cancellationToken),
                _ => Failure("SETTINGS_UNKNOWN_COMMAND"),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure("SETTINGS_UNAVAILABLE");
        }
    }

    private async ValueTask<WebSurfaceCommandResult> GetAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        if (!HasOnlyProperties(payload, "pageId"))
            return Failure("SETTINGS_INVALID_INPUT");

        await EnsureKeyStateAsync().ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        return Success(Snapshot());
    }

    private WebSurfaceCommandResult SetDraft(JsonElement payload, CancellationToken cancellationToken)
    {
        if (!HasOnlyProperties(payload, "pageId", "field", "value")
            || !payload.TryGetProperty("field", out var fieldElement)
            || fieldElement.ValueKind != JsonValueKind.String
            || fieldElement.GetString() is not { } field
            || !payload.TryGetProperty("value", out var value))
            return Failure("SETTINGS_INVALID_INPUT");

        Action<SpeechConnectionViewModel>? update = field switch
        {
            "enabled" when value.ValueKind is JsonValueKind.True or JsonValueKind.False
                => vm => vm.Enabled = value.GetBoolean(),
            "provider" when value.ValueKind == JsonValueKind.String
                && value.GetString() is { } providerName
                && Enum.TryParse<SpeechProviderKind>(providerName, false, out var kind)
                && kind.ToString() == providerName
                => vm => vm.Provider = kind,
            "openAiBaseUrl" when IsNonEmptyString(value, out var openAiBaseUrl)
                => vm => vm.OpenAiBaseUrl = openAiBaseUrl,
            "openAiModel" when IsNonEmptyString(value, out var openAiModel)
                => vm => vm.OpenAiModel = openAiModel,
            "openAiVoice" when IsNonEmptyString(value, out var openAiVoice)
                => vm => vm.OpenAiVoice = openAiVoice,
            "apiKey" when value.ValueKind == JsonValueKind.String && value.GetString() is { } apiKey
                => vm => vm.SetApiKey(apiKey),
            "indexTtsBaseUrl" when IsNonEmptyString(value, out var indexTtsBaseUrl)
                => vm => vm.IndexTtsBaseUrl = indexTtsBaseUrl,
            "voiceName" when value.ValueKind == JsonValueKind.String && value.GetString() is { } voiceName
                => vm => vm.VoiceName = voiceName,
            "autoReadEnabled" when value.ValueKind is JsonValueKind.True or JsonValueKind.False
                => vm => vm.AutoReadEnabled = value.GetBoolean(),
            "doNotDisturb" when value.ValueKind is JsonValueKind.True or JsonValueKind.False
                => vm => vm.DoNotDisturb = value.GetBoolean(),
            "autoReadLimit" when IsWholeNumber(value, out var limit)
                => vm => vm.AutoReadLimit = limit,
            "rate" when value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var rate)
                && double.IsFinite(rate)
                => vm => vm.Rate = rate,
            "volume" when value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var volume)
                && double.IsFinite(volume)
                => vm => vm.Volume = volume,
            _ => null,
        };

        if (update is null)
            return Failure("SETTINGS_INVALID_INPUT");

        cancellationToken.ThrowIfCancellationRequested();
        update(_viewModel);
        cancellationToken.ThrowIfCancellationRequested();
        return Success(Snapshot());
    }

    private WebSurfaceCommandResult SelectVoice(JsonElement payload, CancellationToken cancellationToken)
    {
        if (!HasOnlyProperties(payload, "pageId", "voiceId")
            || !payload.TryGetProperty("voiceId", out var voiceElement)
            || voiceElement.ValueKind != JsonValueKind.String
            || voiceElement.GetString() is not { } voiceId)
            return Failure("SETTINGS_INVALID_INPUT");

        cancellationToken.ThrowIfCancellationRequested();
        var match = _viewModel.Voices.FirstOrDefault(voice => string.Equals(voice.Id, voiceId, StringComparison.Ordinal));
        if (match is null)
            return Failure("SETTINGS_INVALID_INPUT");

        _viewModel.SelectedVoice = match;
        cancellationToken.ThrowIfCancellationRequested();
        return Success(Snapshot());
    }

    private async ValueTask<WebSurfaceCommandResult> SaveAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        if (!HasOnlyProperties(payload, "pageId"))
            return Failure("SETTINGS_INVALID_INPUT");

        cancellationToken.ThrowIfCancellationRequested();
        await _viewModel.SaveCommand.ExecuteAsync(null).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.IsNullOrEmpty(_viewModel.ErrorText))
            return new WebSurfaceCommandResult(false, new { snapshot = Snapshot() }, "SETTINGS_SAVE_FAILED");
        return Success(Snapshot());
    }

    private async ValueTask<WebSurfaceCommandResult> PreviewAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        if (!HasOnlyProperties(payload, "pageId"))
            return Failure("SETTINGS_INVALID_INPUT");

        cancellationToken.ThrowIfCancellationRequested();
        await _viewModel.PreviewCommand.ExecuteAsync(null).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.IsNullOrEmpty(_viewModel.ErrorText))
            return new WebSurfaceCommandResult(false, new { snapshot = Snapshot() }, "SETTINGS_TEST_FAILED");
        return Success(Snapshot());
    }

    private WebSurfaceCommandResult StopPreview(JsonElement payload, CancellationToken cancellationToken)
    {
        if (!HasOnlyProperties(payload, "pageId"))
            return Failure("SETTINGS_INVALID_INPUT");

        cancellationToken.ThrowIfCancellationRequested();
        _viewModel.StopPreview();
        cancellationToken.ThrowIfCancellationRequested();
        return Success(Snapshot());
    }

    private async ValueTask<WebSurfaceCommandResult> ClearKeyAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        if (!HasOnlyProperties(payload, "pageId"))
            return Failure("SETTINGS_INVALID_INPUT");

        cancellationToken.ThrowIfCancellationRequested();
        await _viewModel.ClearKeyCommand.ExecuteAsync(null).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        return Success(Snapshot());
    }

    private async ValueTask<WebSurfaceCommandResult> ImportVoiceAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        // The Web surface never supplies a path. The host-native picker runs on the host thread.
        if (!HasOnlyProperties(payload, "pageId"))
            return Failure("SETTINGS_INVALID_INPUT");

        cancellationToken.ThrowIfCancellationRequested();
        var sourcePath = await _picker.PickWavFileAsync(cancellationToken).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(sourcePath))
            return Success(Snapshot());

        await _viewModel.ImportVoiceAsync(sourcePath).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.IsNullOrEmpty(_viewModel.ErrorText))
            return new WebSurfaceCommandResult(false, new { snapshot = Snapshot() }, "SETTINGS_TEST_FAILED");
        return Success(Snapshot());
    }

    private WebSurfaceCommandResult DeleteVoice(JsonElement payload, CancellationToken cancellationToken)
    {
        if (!HasOnlyProperties(payload, "pageId", "voiceId")
            || !payload.TryGetProperty("voiceId", out var voiceElement)
            || voiceElement.ValueKind != JsonValueKind.String
            || voiceElement.GetString() is not { } voiceId)
            return Failure("SETTINGS_INVALID_INPUT");

        cancellationToken.ThrowIfCancellationRequested();
        var match = _viewModel.Voices.FirstOrDefault(voice => string.Equals(voice.Id, voiceId, StringComparison.Ordinal));
        if (match is null)
            return Failure("SETTINGS_INVALID_INPUT");

        _viewModel.SelectedVoice = match;
        _viewModel.DeleteSelectedVoice();
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.IsNullOrEmpty(_viewModel.ErrorText))
            return new WebSurfaceCommandResult(false, new { snapshot = Snapshot() }, "SETTINGS_SAVE_FAILED");
        return Success(Snapshot());
    }

    private object Snapshot() => new
    {
        enabled = _viewModel.Enabled,
        providers = _viewModel.Providers.Select(provider => new
        {
            id = provider.Provider.ToString(),
            displayName = provider.DisplayName,
        }).ToArray(),
        provider = _viewModel.Provider.ToString(),
        isOpenAiCompatible = _viewModel.IsOpenAiCompatible,
        isIndexTts = _viewModel.IsIndexTts,
        openAiBaseUrl = _viewModel.OpenAiBaseUrl,
        openAiModel = _viewModel.OpenAiModel,
        openAiVoice = _viewModel.OpenAiVoice,
        keyStateText = _viewModel.KeyStateText,
        isKeySaved = _viewModel.IsKeySaved,
        indexTtsBaseUrl = _viewModel.IndexTtsBaseUrl,
        voices = _viewModel.Voices.Select(voice => new
        {
            id = voice.Id,
            name = voice.Name,
        }).ToArray(),
        selectedVoiceId = _viewModel.SelectedVoice?.Id ?? string.Empty,
        voiceName = _viewModel.VoiceName,
        voiceCount = _viewModel.Voices.Count,
        voiceLimit = VoiceLimit,
        autoReadEnabled = _viewModel.AutoReadEnabled,
        doNotDisturb = _viewModel.DoNotDisturb,
        autoReadLimit = _viewModel.AutoReadLimit,
        rate = _viewModel.Rate,
        volume = _viewModel.Volume,
        isBusy = _viewModel.IsBusy,
        statusText = _viewModel.StatusText,
        errorText = _viewModel.ErrorText,
    };

    private Task EnsureKeyStateAsync()
    {
        lock (_initGate)
        {
            _initTask ??= _viewModel.InitializeAsync(CancellationToken.None);
            return _initTask;
        }
    }

    private static bool IsNonEmptyString(JsonElement element, out string value)
    {
        if (element.ValueKind == JsonValueKind.String && element.GetString() is { } text)
        {
            value = text;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool IsWholeNumber(JsonElement element, out int value)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var raw) && double.IsFinite(raw)
            && Math.Abs(raw - Math.Round(raw)) < 1e-9 && raw is >= 0 and <= int.MaxValue)
        {
            value = (int)Math.Round(raw);
            return true;
        }

        value = 0;
        return false;
    }

    private static bool TryReadPageId(JsonElement payload, out string pageId)
    {
        pageId = string.Empty;
        return payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("pageId", out var element)
            && element.ValueKind == JsonValueKind.String
            && (pageId = element.GetString() ?? string.Empty).Length > 0;
    }

    private static bool HasOnlyProperties(JsonElement payload, params string[] allowedNames)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return false;
        var allowed = new HashSet<string>(allowedNames, StringComparer.Ordinal);
        return payload.EnumerateObject().All(property => allowed.Contains(property.Name))
            && allowedNames.All(name => payload.TryGetProperty(name, out _));
    }

    private static WebSurfaceCommandResult Success(object payload) => new(true, payload);

    private static WebSurfaceCommandResult Failure(string errorCode) => new(false, ErrorCode: errorCode);
}
