using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FgoPet.Core.Speech;
using FgoPet.Core.Secrets;

namespace FgoPet.Infrastructure.Speech;

internal static class SpeechHttp
{
    public static Uri Resolve(Uri endpoint, string resource, bool loopbackOnly)
    {
        if (endpoint is null || !endpoint.IsAbsoluteUri || endpoint.Scheme is not ("http" or "https"))
        {
            throw new SpeechSynthesisException(SpeechFailureCategory.Configuration, "语音服务地址无效。");
        }

        if (loopbackOnly)
        {
            if (endpoint.Scheme != Uri.UriSchemeHttp || !endpoint.IsLoopback)
            {
                throw new SpeechSynthesisException(SpeechFailureCategory.Configuration, "GPT-SoVITS 只允许连接本机 HTTP 服务。");
            }
        }
        else if (endpoint.Scheme == Uri.UriSchemeHttp && !endpoint.IsLoopback)
        {
            throw new SpeechSynthesisException(SpeechFailureCategory.Configuration, "非本机语音服务必须使用 HTTPS。");
        }

        return new Uri(endpoint.ToString().TrimEnd('/') + "/" + resource, UriKind.Absolute);
    }

    public static async Task<SpeechSynthesisResult> ReadWaveAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new SpeechSynthesisException(
                response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? SpeechFailureCategory.Authentication
                    : response.StatusCode >= HttpStatusCode.InternalServerError
                        ? SpeechFailureCategory.ServiceUnavailable
                        : SpeechFailureCategory.InvalidResponse,
                "语音服务未返回成功结果。",
                statusCode: response.StatusCode,
                requestWasSent: true);
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return new SpeechSynthesisResult(bytes, "audio/wav");
        }
        catch (ArgumentException error)
        {
            throw new SpeechSynthesisException(
                SpeechFailureCategory.InvalidResponse,
                "语音服务返回的不是 WAV 音频。",
                error,
                response.StatusCode,
                requestWasSent: true);
        }
    }

    public static SpeechSynthesisException Network(Exception error) =>
        new(SpeechFailureCategory.Network, "无法连接语音服务。", error);
}

public sealed class OpenAiCompatibleSpeechSynthesizer : ISpeechSynthesizer
{
    private readonly ICredentialReader _credentialReader;
    private readonly HttpClient _httpClient;

    public OpenAiCompatibleSpeechSynthesizer(ICredentialReader credentialReader, HttpClient httpClient)
    {
        _credentialReader = credentialReader ?? throw new ArgumentNullException(nameof(credentialReader));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public SpeechProviderKind Provider => SpeechProviderKind.OpenAiCompatible;

    public async Task<SpeechSynthesisResult> SynthesizeAsync(
        SpeechSynthesisRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Provider != Provider)
        {
            throw new SpeechSynthesisException(SpeechFailureCategory.Configuration, "语音 provider 与请求不匹配。");
        }

        var endpoint = SpeechHttp.Resolve(request.Endpoint, "audio/speech", loopbackOnly: false);
        var target = string.IsNullOrWhiteSpace(request.CredentialTarget)
            ? "fgo-pet/speech/openai"
            : request.CredentialTarget;
        var apiKey = await _credentialReader.ReadAsync(target, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new SpeechSynthesisException(SpeechFailureCategory.Authentication, "尚未配置语音服务凭据。");
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        httpRequest.Content = new StringContent(JsonSerializer.Serialize(new
        {
            model = string.IsNullOrWhiteSpace(request.Model) ? "gpt-4o-mini-tts" : request.Model,
            input = request.Text,
            voice = string.IsNullOrWhiteSpace(request.Voice) ? "alloy" : request.Voice,
            response_format = "wav",
        }), Encoding.UTF8, "application/json");

        try
        {
            using var response = await _httpClient.SendAsync(
                httpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            return await SpeechHttp.ReadWaveAsync(response, cancellationToken).ConfigureAwait(false);
        }
        catch (SpeechSynthesisException)
        {
            throw;
        }
        catch (HttpRequestException error)
        {
            throw SpeechHttp.Network(error);
        }
    }
}

public sealed class GptSoVitsSpeechSynthesizer : ISpeechSynthesizer
{
    private readonly HttpClient _httpClient;

    public GptSoVitsSpeechSynthesizer(HttpClient httpClient) =>
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    public SpeechProviderKind Provider => SpeechProviderKind.GptSoVits;

    public async Task<SpeechSynthesisResult> SynthesizeAsync(
        SpeechSynthesisRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Provider != Provider)
        {
            throw new SpeechSynthesisException(SpeechFailureCategory.Configuration, "语音 provider 与请求不匹配。");
        }

        var endpoint = SpeechHttp.Resolve(request.Endpoint, "tts", loopbackOnly: true);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint);
        httpRequest.Content = new StringContent(JsonSerializer.Serialize(new
        {
            text = request.Text,
            text_lang = string.IsNullOrWhiteSpace(request.Language) ? "zh" : request.Language,
            ref_audio_path = request.ReferenceAudioPath,
            prompt_text = request.PromptText,
            prompt_lang = string.IsNullOrWhiteSpace(request.PromptLanguage) ? request.Language : request.PromptLanguage,
            media_type = "wav",
            streaming_mode = false,
        }), Encoding.UTF8, "application/json");

        try
        {
            using var response = await _httpClient.SendAsync(
                httpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            return await SpeechHttp.ReadWaveAsync(response, cancellationToken).ConfigureAwait(false);
        }
        catch (SpeechSynthesisException)
        {
            throw;
        }
        catch (HttpRequestException error)
        {
            throw SpeechHttp.Network(error);
        }
    }
}

public sealed class SpeechSynthesizerRouter : ISpeechSynthesizer
{
    private readonly OpenAiCompatibleSpeechSynthesizer _openAi;
    private readonly GptSoVitsSpeechSynthesizer _gptSoVits;

    public SpeechSynthesizerRouter(
        OpenAiCompatibleSpeechSynthesizer openAi,
        GptSoVitsSpeechSynthesizer gptSoVits)
    {
        _openAi = openAi ?? throw new ArgumentNullException(nameof(openAi));
        _gptSoVits = gptSoVits ?? throw new ArgumentNullException(nameof(gptSoVits));
    }

    public SpeechProviderKind Provider =>
        throw new InvalidOperationException("Router requires a provider in each request.");

    public Task<SpeechSynthesisResult> SynthesizeAsync(
        SpeechSynthesisRequest request,
        CancellationToken cancellationToken = default) =>
        request.Provider switch
        {
            SpeechProviderKind.OpenAiCompatible => _openAi.SynthesizeAsync(request, cancellationToken),
            SpeechProviderKind.GptSoVits => _gptSoVits.SynthesizeAsync(request, cancellationToken),
            _ => throw new SpeechSynthesisException(SpeechFailureCategory.Configuration, "未知语音 provider。"),
        };
}