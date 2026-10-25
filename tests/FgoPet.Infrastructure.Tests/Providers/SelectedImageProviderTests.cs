using System.Net;
using System.Text;
using System.Text.Json;
using FgoPet.Core.Dialogue;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Providers;
using FgoPet.Kernel.Agent;
using Xunit;

namespace FgoPet.Infrastructure.Tests.Providers;

public sealed class SelectedImageProviderTests
{
    private static readonly ToolScope Scope = new("conversation", "role", null);
    private static readonly ModelRouteKey Route = ModelRouteKey.From(new("openai", "https://api.openai.com/v1", "gpt-4.1-mini"));
    [Theory]
    [InlineData("openai", "https://api.openai.com/v1", "gpt-4.1-mini")]
    [InlineData("deepseek", "https://api.deepseek.com", "deepseek-flash")]
    [InlineData("deepseek", "https://api.deepseek.com/v1", "deepseek-flash")]
    [InlineData("custom-openai-compatible", "http://127.0.0.1:8326/v1", "buddy/deepseek-v4.1-flash")]
    public async Task Actual_http_body_contains_selected_pixels_in_user_content_after_tool_results(string providerId, string endpoint, string modelId)
    {
        var route = ModelRouteKey.From(new(providerId, endpoint, modelId));
        var store = Store(route); using var handler = new HttpFixture(); using var http = new HttpClient(handler);
        var provider = new OpenAiCompatibleChatProvider(providerId, new(endpoint), modelId, new Credentials(), http, images: store);
        await foreach (var _ in provider.StreamAsync(Request(store, route), default)) { }
        using var body = JsonDocument.Parse(handler.Body!);
        var messages = body.RootElement.GetProperty("messages");
        Assert.Equal("tool", messages[2].GetProperty("role").GetString());
        Assert.Equal("user", messages[3].GetProperty("role").GetString());
        Assert.StartsWith("data:image/png;base64,", messages[3].GetProperty("content")[1].GetProperty("image_url").GetProperty("url").GetString());
        Assert.DoesNotContain("file:", handler.Body);
        Assert.Equal("low", messages[3].GetProperty("content")[1].GetProperty("image_url").GetProperty("detail").GetString());
        Assert.Equal(modelId, body.RootElement.GetProperty("model").GetString());
        Assert.Equal(endpoint + "/chat/completions", handler.Uri!.AbsoluteUri);
        Assert.Equal(1, handler.Sends);
    }
    [Theory]
    [InlineData("openai", "https://api.openai.com/v1", "gpt-4.1-mini")]
    [InlineData("deepseek", "https://api.deepseek.com", "deepseek-flash")]
    [InlineData("deepseek", "https://api.deepseek.com/v1", "deepseek-flash")]
    [InlineData("custom-openai-compatible", "http://127.0.0.1:8326/v1", "buddy/deepseek-v4.1-flash")]
    public async Task Async_credential_wait_revocation_prevents_actual_http_dispatch(string providerId, string endpoint, string modelId)
    {
        var route = ModelRouteKey.From(new(providerId, endpoint, modelId));
        var store = Store(route); using var handler = new HttpFixture(); using var http = new HttpClient(handler);
        var provider = new OpenAiCompatibleChatProvider(providerId, new(endpoint), modelId, new Credentials(store.Clear), http, images: store);
        await Assert.ThrowsAsync<SelectedImageException>(async () => { await foreach (var _ in provider.StreamAsync(Request(store, route), default)) { } });
        Assert.Equal(0, handler.Sends); Assert.Null(handler.Body);
    }
    [Theory]
    [InlineData("custom-openai-compatible", "https://fixture.test/v1", "gpt-4.1-mini")]
    [InlineData("deepseek", "https://api.deepseek.com/v1", "deepseek-v4-pro")]
    [InlineData("deepseek", "https://fixture.test/v1", "deepseek-flash")]
    [InlineData("custom-openai-compatible", "http://127.0.0.1:8327/v1", "buddy/deepseek-v4.1-flash")]
    [InlineData("openai", "http://127.0.0.1:8326/v1", "buddy/deepseek-v4.1-flash")]
    [InlineData("custom-openai-compatible", "http://127.0.0.1:8326/v1", "buddy/deepseek-v4.1-flash-custom")]
    public async Task Unsupported_route_is_rejected_before_credentials_or_http(string providerId, string endpoint, string modelId)
    {
        var store = Store(); using var handler = new HttpFixture(); using var http = new HttpClient(handler);
        var credentials = new Credentials();
        var provider = new OpenAiCompatibleChatProvider(providerId, new(endpoint), modelId, credentials, http, images: store);
        var error = await Assert.ThrowsAsync<SelectedImageException>(async () => { await foreach (var _ in provider.StreamAsync(Request(store), default)) { } });
        Assert.Equal("IMAGE_MODEL_UNSUPPORTED", error.Code); Assert.Equal(0, handler.Sends); Assert.Equal(0, credentials.Reads);
    }
    [Fact]
    public async Task Buddy_cancellation_during_credential_wait_prevents_http_dispatch()
    {
        var route = ModelRouteKey.From(new("custom-openai-compatible", "http://127.0.0.1:8326/v1", "buddy/deepseek-v4.1-flash"));
        var store = Store(route); using var handler = new HttpFixture(); using var http = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var provider = new OpenAiCompatibleChatProvider(route.ProviderId, new("http://127.0.0.1:8326/v1"), route.ModelId,
            new Credentials(cancellation.Cancel), http, images: store);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        { await foreach (var _ in provider.StreamAsync(Request(store, route), cancellation.Token)) { } });
        Assert.Equal(0, handler.Sends);
    }

    private static SelectedImageStore Store(ModelRouteKey? route = null)
    {
        var store = new SelectedImageStore(s => s == Scope);
        store.Grant(Scope, route ?? Route, new(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="), 1, 1), "user");
        store.Bind(Scope, route ?? Route, "user", "run", default);
        return store;
    }
    private static ChatRequest Request(SelectedImageStore store, ModelRouteKey? route = null) => ChatRequest.CreateAgent("role", "conversation", [
        new(ModelMessageRole.User, "synthetic fixture", []),
        new(ModelMessageRole.Assistant, "", [new("call", "image.read", "{}")]),
        new(ModelMessageRole.Tool, "{}", [], "call") { Image = store.Read(Scope, route ?? Route, "run", default) }
    ]);
    private sealed class Credentials(Action? after = null) : FgoPet.Infrastructure.Secrets.ICredentialReader
    {
        public int Reads;
        public async Task<string?> ReadAsync(string target, CancellationToken token)
        { Reads++; await Task.Yield(); token.ThrowIfCancellationRequested(); after?.Invoke(); return "synthetic-key"; }
    }
    private sealed class HttpFixture : HttpMessageHandler
    {
        public int Sends; public string? Body; public Uri? Uri;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Sends++; Uri = request.RequestUri; Body = await request.Content!.ReadAsStringAsync(token);
            return new(HttpStatusCode.OK) { Content = new StringContent("data: {\"choices\":[{\"delta\":{\"content\":\"synthetic fixture\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream") };
        }
    }
}
