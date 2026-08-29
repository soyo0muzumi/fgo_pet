using System.Net;
using System.Net.Http.Json;
using FgoPet.Core.Dialogue;
using FgoPet.Infrastructure.Providers;
using FgoPet.Infrastructure.Secrets;
using Xunit;

namespace FgoPet.Infrastructure.Tests.Providers;

public sealed class OpenAiCompatibleChatProviderTests
{
    [Fact]
    public async Task Model_discovery_returns_ids_without_logging_the_key()
    {
        var handler = new RecordingHandler(
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { data = new[] { new { id = "deepseek-chat" } } }),
            });
        var provider = CreateProvider(handler, "secret-not-to-log");

        var models = await provider.ListModelsAsync(CancellationToken.None);

        Assert.Equal(new[] { "deepseek-chat" }, models.Select(model => model.Id));
        Assert.DoesNotContain("secret-not-to-log", handler.RequestLog);
        Assert.Equal("Bearer secret-not-to-log", handler.AuthorizationHeader);
    }

    [Fact]
    public async Task Streaming_completion_parses_deltas_until_done()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "data: {\"choices\":[{\"delta\":{\"content\":\"你好\"}}]}\n\n" +
                "data: {\"choices\":[{\"delta\":{\"content\":\"，御主\"}}]}\n\n" +
                "data: [DONE]\n\n"),
        });
        var provider = CreateProvider(handler, "secret-value");
        var request = new ChatRequest(
            "800100",
            "conversation-1",
            new[] { new PromptMessage(ChatMessageRole.User, "你好") });

        var chunks = new List<ChatStreamChunk>();
        await foreach (var chunk in provider.StreamAsync(request, CancellationToken.None))
        {
            chunks.Add(chunk);
        }

        Assert.Equal("你好，御主", string.Concat(chunks.Select(chunk => chunk.TextDelta)));
        Assert.True(chunks[^1].IsComplete);
        Assert.Contains("/chat/completions", handler.RequestLog);
    }

    [Fact]
    public void Non_loopback_http_endpoint_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new OpenAiCompatibleChatProvider(
            "deepseek",
            new Uri("http://api.deepseek.com/v1"),
            "deepseek-chat",
            new FakeCredentialReader("secret"),
            new HttpClient()));
    }

    private static OpenAiCompatibleChatProvider CreateProvider(RecordingHandler handler, string secret) =>
        new(
            "deepseek",
            new Uri("https://api.deepseek.com/v1"),
            "deepseek-chat",
            new FakeCredentialReader(secret),
            new HttpClient(handler));

    private sealed class FakeCredentialReader(string secret) : ICredentialReader
    {
        public Task<string?> ReadAsync(string target, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<string?>(secret);
        }
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public string RequestLog { get; private set; } = string.Empty;
        public string? AuthorizationHeader { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            AuthorizationHeader = request.Headers.Authorization?.ToString();
            RequestLog = $"{request.Method} {request.RequestUri} {request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult()}";
            return Task.FromResult(responseFactory(request));
        }
    }
}
