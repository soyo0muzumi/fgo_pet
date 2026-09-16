using System.Net;
using System.Text;
using System.Text.Json;
using FgoPet.Core.Speech;
using FgoPet.Infrastructure.Speech;
using Xunit;

namespace FgoPet.Speech.Infrastructure.Tests;

public sealed class IndexTtsSpeechSynthesizerTests
{
    [Fact]
    public async Task Non_loopback_is_rejected_before_any_request()
    {
        var handler = new Handler(_ => throw new InvalidOperationException("No request allowed"));
        using var client = new HttpClient(handler);
        var service = new IndexTtsSpeechSynthesizer(client);
        var error = await Assert.ThrowsAsync<SpeechSynthesisException>(() => service.SynthesizeAsync(
            new("test", SpeechProviderKind.IndexTts, new Uri("http://example.com"), referenceAudioPath: "unused")));
        Assert.Equal(SpeechFailureCategory.Configuration, error.Category);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Incompatible_config_does_not_upload_reference()
    {
        using var client = new HttpClient(new Handler(_ => Json(new { dependencies = Array.Empty<object>() })));
        var error = await Assert.ThrowsAsync<SpeechSynthesisException>(() => new IndexTtsSpeechSynthesizer(client)
            .SynthesizeAsync(new("test", SpeechProviderKind.IndexTts, new Uri("http://127.0.0.1:7860"), referenceAudioPath: "not-opened")));
        Assert.Equal(SpeechFailureCategory.Configuration, error.Category);
    }

    [Fact]
    public async Task Clone_uploads_reference_and_fetches_audio_from_configured_origin_only()
    {
        var path = Path.Combine(Path.GetTempPath(), "index-test-" + Guid.NewGuid().ToString("N") + ".wav");
        var wave = Encoding.ASCII.GetBytes("RIFF0000WAVE");
        await File.WriteAllBytesAsync(path, wave);
        var seen = new List<string>();
        try
        {
            using var client = new HttpClient(new Handler(request =>
            {
                var url = request.RequestUri!;
                Assert.Equal("127.0.0.1", url.Host);
                seen.Add(url.AbsolutePath);
                if (url.AbsolutePath == "/config") return Config();
                if (url.AbsolutePath.EndsWith("/upload")) return Json(new[] { "/tmp/reference.wav" });
                if (url.AbsolutePath == "/gradio_api/call/gen_single")
                {
                    using var body = JsonDocument.Parse(request.Options.TryGetValue(new HttpRequestOptionsKey<string>("test-body"), out var text) ? text : "");
                    Assert.Equal("test", body.RootElement.GetProperty("data")[2].GetString());
                    Assert.Equal("/tmp/reference.wav", body.RootElement.GetProperty("data")[1].GetProperty("path").GetString());
                    return Json(new { event_id = "abc123" });
                }
                if (url.AbsolutePath.EndsWith("/abc123"))
                    return new(HttpStatusCode.OK) { Content = new StringContent("event: complete\ndata: [{\"value\":{\"path\":\"/tmp/out.wav\",\"url\":\"https://untrusted.test/file.wav\"}}]\n\n") };
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(wave) };
            }));
            var result = await new IndexTtsSpeechSynthesizer(client).SynthesizeAsync(
                new("test", SpeechProviderKind.IndexTts, new Uri("http://127.0.0.1:7860"), referenceAudioPath: path));
            Assert.Equal(wave, result.WavBytes);
            Assert.Equal(5, seen.Count);
        }
        finally { File.Delete(path); }
    }

    private static HttpResponseMessage Config()
    {
        var types = new[] { "radio", "audio", "textbox", "dropdown", "audio", "slider" };
        return Json(new
        {
            dependencies = new[] { new { api_name = "gen_single", inputs = Enumerable.Range(0, 26).ToArray() } },
            components = Enumerable.Range(0, 26).Select(i => new
            {
                id = i, type = i < types.Length ? types[i] : "slider",
                props = new { value = (object?)null, choices = new[] { new[] { "same", "same" } } }
            }).ToArray()
        });
    }
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value)) };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Content is not null)
                request.Options.Set(new HttpRequestOptionsKey<string>("test-body"), await request.Content.ReadAsStringAsync(cancellationToken));
            return reply(request);
        }
    }
}
