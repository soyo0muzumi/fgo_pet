using System.Net;
using System.Text.Json;
using FgoPet.Core.Speech;
using FgoPet.Core.Secrets;
using FgoPet.Infrastructure.Speech;
using Xunit;

namespace FgoPet.Speech.Infrastructure.Tests;

public sealed class SpeechSynthesizerTests
{
    [Fact]
    public async Task OpenAi_speech_posts_to_audio_endpoint_with_credential_and_wav_format()
    {
        Uri? capturedUri = null;
        string? capturedScheme = null;
        string? capturedParameter = null;
        string? capturedBody = null;
        using var client = new HttpClient(new StubHandler(async request =>
        {
            capturedUri = request.RequestUri;
            capturedScheme = request.Headers.Authorization?.Scheme;
            capturedParameter = request.Headers.Authorization?.Parameter;
            capturedBody = request.Content is null ? null : await request.Content.ReadAsStringAsync();
            return WaveResponse();
        }));
        var synthesizer = new OpenAiCompatibleSpeechSynthesizer(
            new StubCredentialReader("speech-secret"),
            client);

        var result = await synthesizer.SynthesizeAsync(new SpeechSynthesisRequest(
            "你好，御主。",
            SpeechProviderKind.OpenAiCompatible,
            new Uri("https://api.example.test/v1"),
            model: "tts-model",
            voice: "alloy",
            credentialTarget: "fgo-pet/speech/test"));

        Assert.Equal("/v1/audio/speech", capturedUri!.AbsolutePath);
        Assert.Equal("Bearer", capturedScheme);
        Assert.Equal("speech-secret", capturedParameter);
        using var json = JsonDocument.Parse(capturedBody!);
        Assert.Equal("wav", json.RootElement.GetProperty("response_format").GetString());
        Assert.Equal("你好，御主。", json.RootElement.GetProperty("input").GetString());
        Assert.Equal(12, result.WavBytes.Length);
    }


    private static HttpResponseMessage WaveResponse() => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent([
            (byte)'R', (byte)'I', (byte)'F', (byte)'F', 36, 0, 0, 0,
            (byte)'W', (byte)'A', (byte)'V', (byte)'E']),
    };

    private sealed class StubCredentialReader(string secret) : ICredentialReader
    {
        public Task<string?> ReadAsync(string target, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(secret);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request);
    }
}