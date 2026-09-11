using System.Net;

namespace FgoPet.Core.Speech;

public enum SpeechProviderKind
{
    OpenAiCompatible,
    GptSoVits,
    IndexTts,
}

public enum SpeechFailureCategory
{
    Configuration,
    Authentication,
    Network,
    ServiceUnavailable,
    InvalidResponse,
    Cancelled,
}

public enum SpeechPlaybackState
{
    Idle,
    Preparing,
    Playing,
    Ended,
    Failed,
    Stopped,
}

public sealed record SpeechPlaybackOptions(double Rate = 1.0, double Volume = 1.0)
{
    public double SafeRate => Math.Clamp(double.IsFinite(Rate) ? Rate : 1.0, 0.5, 2.0);
    public double SafeVolume => Math.Clamp(double.IsFinite(Volume) ? Volume : 1.0, 0.0, 1.0);
}

public sealed class SpeechSynthesisException : Exception
{
    public SpeechSynthesisException(
        SpeechFailureCategory category,
        string message,
        Exception? innerException = null,
        HttpStatusCode? statusCode = null,
        bool requestWasSent = false)
        : base(message, innerException)
    {
        Category = category;
        StatusCode = statusCode;
        RequestWasSent = requestWasSent;
    }

    public SpeechFailureCategory Category { get; }
    public HttpStatusCode? StatusCode { get; }
    public bool RequestWasSent { get; }
}

/// <summary>Non-secret speech configuration. API keys remain in Credential Manager.</summary>
public sealed record ReferenceVoice(string Id, string Name, string AudioPath);

public sealed record SpeechConnectionSettings
{
    public bool Enabled { get; init; }
    public SpeechProviderKind Provider { get; init; } = SpeechProviderKind.OpenAiCompatible;
    public string OpenAiBaseUrl { get; init; } = "https://api.openai.com/v1";
    public string OpenAiModel { get; init; } = "gpt-4o-mini-tts";
    public string OpenAiVoice { get; init; } = "alloy";
    public string OpenAiCredentialTarget { get; init; } = "fgo-pet/speech/openai";
    public string GptSoVitsBaseUrl { get; init; } = "http://127.0.0.1:9880";
    public string GptSoVitsReferenceAudioPath { get; init; } = string.Empty;
    public string GptSoVitsPromptText { get; init; } = string.Empty;
    public string GptSoVitsLanguage { get; init; } = "zh";
    public string GptSoVitsPromptLanguage { get; init; } = "zh";
    public string IndexTtsBaseUrl { get; init; } = "http://127.0.0.1:7860";
    public string IndexTtsVoiceId { get; init; } = string.Empty;
    public IReadOnlyList<ReferenceVoice> IndexTtsVoices { get; init; } = Array.Empty<ReferenceVoice>();
    public bool AutoReadEnabled { get; init; }
    public bool DoNotDisturb { get; init; }
    public int AutoReadLimit { get; init; } = 300;
    public double Rate { get; init; } = 1.0;
    public double Volume { get; init; } = 1.0;

    public static SpeechConnectionSettings Defaults { get; } = new();

    public SpeechPlaybackOptions Playback => new(Rate, Volume);

    public SpeechConnectionSettings Normalize() => this with
    {
        IndexTtsBaseUrl = Bounded(IndexTtsBaseUrl, Defaults.IndexTtsBaseUrl, 512),
        IndexTtsVoiceId = Bounded(IndexTtsVoiceId, string.Empty, 64),
        IndexTtsVoices = (IndexTtsVoices ?? Array.Empty<ReferenceVoice>()).Where(v => v is not null && !string.IsNullOrWhiteSpace(v.Id)
            && !string.IsNullOrWhiteSpace(v.Name) && !string.IsNullOrWhiteSpace(v.AudioPath)).Take(32).ToArray(),
        OpenAiBaseUrl = Bounded(OpenAiBaseUrl, Defaults.OpenAiBaseUrl, 512),
        OpenAiModel = Bounded(OpenAiModel, Defaults.OpenAiModel, 128),
        OpenAiVoice = Bounded(OpenAiVoice, Defaults.OpenAiVoice, 128),
        OpenAiCredentialTarget = Bounded(OpenAiCredentialTarget, Defaults.OpenAiCredentialTarget, 256),
        GptSoVitsBaseUrl = Bounded(GptSoVitsBaseUrl, Defaults.GptSoVitsBaseUrl, 512),
        GptSoVitsReferenceAudioPath = Bounded(GptSoVitsReferenceAudioPath, string.Empty, 512),
        GptSoVitsPromptText = Bounded(GptSoVitsPromptText, string.Empty, 1_000),
        GptSoVitsLanguage = Bounded(GptSoVitsLanguage, Defaults.GptSoVitsLanguage, 32),
        GptSoVitsPromptLanguage = Bounded(GptSoVitsPromptLanguage, Defaults.GptSoVitsPromptLanguage, 32),
        AutoReadLimit = Math.Clamp(AutoReadLimit <= 0 ? 300 : AutoReadLimit, 1, 300),
        Rate = Math.Clamp(double.IsFinite(Rate) ? Rate : 1.0, 0.5, 2.0),
        Volume = Math.Clamp(double.IsFinite(Volume) ? Volume : 1.0, 0.0, 1.0),
    };

    public SpeechSynthesisRequest CreateRequest()
    {
        var settings = Normalize();
        if (!settings.Enabled)
        {
            throw new SpeechSynthesisException(SpeechFailureCategory.Configuration, "角色朗读尚未启用。请先完成朗读设置。");
        }

        try
        {
            return settings.Provider switch
            {
                SpeechProviderKind.OpenAiCompatible => new SpeechSynthesisRequest(
                    "试听",
                    settings.Provider,
                    new Uri(settings.OpenAiBaseUrl, UriKind.Absolute),
                    model: settings.OpenAiModel,
                    voice: settings.OpenAiVoice,
                    credentialTarget: settings.OpenAiCredentialTarget),
                SpeechProviderKind.IndexTts => new SpeechSynthesisRequest(
                    "试听", settings.Provider, new Uri(settings.IndexTtsBaseUrl, UriKind.Absolute),
                    referenceAudioPath: settings.IndexTtsVoices.FirstOrDefault(v => v.Id == settings.IndexTtsVoiceId)?.AudioPath),
                SpeechProviderKind.GptSoVits => new SpeechSynthesisRequest(
                    "试听",
                    settings.Provider,
                    new Uri(settings.GptSoVitsBaseUrl, UriKind.Absolute),
                    language: settings.GptSoVitsLanguage,
                    referenceAudioPath: settings.GptSoVitsReferenceAudioPath,
                    promptText: settings.GptSoVitsPromptText,
                    promptLanguage: settings.GptSoVitsPromptLanguage),
                _ => throw new SpeechSynthesisException(SpeechFailureCategory.Configuration, "未知语音服务。"),
            };
        }
        catch (UriFormatException error)
        {
            throw new SpeechSynthesisException(SpeechFailureCategory.Configuration, "语音服务地址无效。", error);
        }
    }

    private static string Bounded(string? value, string fallback, int maxLength)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized)) return fallback;
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }
}

public sealed record SpeechSynthesisRequest
{
    public const int MaxTextLength = 4_000;

    public SpeechSynthesisRequest(
        string text,
        SpeechProviderKind provider,
        Uri endpoint,
        string? model = null,
        string? voice = null,
        string? language = null,
        string? referenceAudioPath = null,
        string? promptText = null,
        string? promptLanguage = null,
        string? credentialTarget = null)
    {
        Text = RequiredText(text, nameof(text), MaxTextLength);
        Provider = provider;
        Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        Model = Optional(model, nameof(model), 128);
        Voice = Optional(voice, nameof(voice), 128);
        Language = Optional(language, nameof(language), 32);
        ReferenceAudioPath = Optional(referenceAudioPath, nameof(referenceAudioPath), 512);
        PromptText = Optional(promptText, nameof(promptText), 1_000);
        PromptLanguage = Optional(promptLanguage, nameof(promptLanguage), 32);
        CredentialTarget = Optional(credentialTarget, nameof(credentialTarget), 256);
    }

    public string Text { get; }
    public SpeechProviderKind Provider { get; }
    public Uri Endpoint { get; }
    public string Model { get; }
    public string Voice { get; }
    public string Language { get; }
    public string ReferenceAudioPath { get; }
    public string PromptText { get; }
    public string PromptLanguage { get; }
    public string CredentialTarget { get; }

    public SpeechSynthesisRequest WithText(string text) => new(
        text,
        Provider,
        Endpoint,
        Model,
        Voice,
        Language,
        ReferenceAudioPath,
        PromptText,
        PromptLanguage,
        CredentialTarget);

    private static string RequiredText(string value, string parameterName, int maxLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim();
        if (normalized.Length > maxLength)
        {
            throw new ArgumentException($"{parameterName} must be at most {maxLength} characters.", parameterName);
        }

        return normalized;
    }

    private static string Optional(string? value, string parameterName, int maxLength) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : RequiredText(value, parameterName, maxLength);
}

public sealed record SpeechSynthesisResult
{
    public SpeechSynthesisResult(byte[] wavBytes, string contentType = "audio/wav")
    {
        if (wavBytes is null || wavBytes.Length < 12 || !IsWave(wavBytes))
        {
            throw new ArgumentException("Speech output must be a RIFF/WAVE payload.", nameof(wavBytes));
        }

        WavBytes = wavBytes.ToArray();
        ContentType = string.IsNullOrWhiteSpace(contentType) ? "audio/wav" : contentType.Trim();
    }

    public byte[] WavBytes { get; }
    public string ContentType { get; }

    public static bool IsWave(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 12
        && bytes[0] == (byte)'R'
        && bytes[1] == (byte)'I'
        && bytes[2] == (byte)'F'
        && bytes[3] == (byte)'F'
        && bytes[8] == (byte)'W'
        && bytes[9] == (byte)'A'
        && bytes[10] == (byte)'V'
        && bytes[11] == (byte)'E';
}

public interface ISpeechSynthesizer
{
    SpeechProviderKind Provider { get; }
    Task<SpeechSynthesisResult> SynthesizeAsync(
        SpeechSynthesisRequest request,
        CancellationToken cancellationToken = default);
}

public interface ISpeechAudioPlayer : IDisposable
{
    Task PlayAsync(SpeechSynthesisResult audio, SpeechPlaybackOptions options, CancellationToken cancellationToken = default);
    void Stop();
}