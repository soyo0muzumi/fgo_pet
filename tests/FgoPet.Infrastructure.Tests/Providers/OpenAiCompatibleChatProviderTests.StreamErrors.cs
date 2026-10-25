using System.Net;
using System.Text.Json;
using FgoPet.Core.Dialogue;
using FgoPet.Infrastructure.Providers;
using Xunit;

namespace FgoPet.Infrastructure.Tests.Providers;

public sealed partial class OpenAiCompatibleChatProviderTests
{
    [Theory]
    [InlineData(502, ProviderFailureCategory.ServiceUnavailable)]
    [InlineData(403, ProviderFailureCategory.Authentication)]
    [InlineData(429, ProviderFailureCategory.RateLimited)]
    [InlineData(400, ProviderFailureCategory.InvalidResponse)]
    public async Task In_stream_provider_error_preserves_embedded_status_and_sent_state(int status, ProviderFailureCategory category)
    {
        var handler = new RecordingHandler(_ => StreamResponse(ErrorFrame(status) + "data: [DONE]\n\n"));
        var error = await Assert.ThrowsAsync<ProviderRequestException>(() => StreamAll(CreateProvider(handler, "fixture"), BuildToolRequest()));
        Assert.Equal(category, error.Category);
        Assert.Equal((HttpStatusCode)status, error.HttpStatusCode);
        Assert.Equal("upstream_error", error.ProviderCode);
        Assert.True(error.RequestWasSent);
        Assert.DoesNotContain("SYNTHETIC_PRIVATE_ERROR_DETAIL", error.Message);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(200)]
    [InlineData(999)]
    public async Task In_stream_error_never_accepts_invalid_or_success_embedded_status(int? status)
    {
        var handler = new RecordingHandler(_ => StreamResponse(ErrorFrame(status) + "data: [DONE]\n\n"));
        var error = await Assert.ThrowsAsync<ProviderRequestException>(() => StreamAll(CreateProvider(handler, "fixture"), BuildToolRequest()));
        Assert.Equal(ProviderFailureCategory.ServiceUnavailable, error.Category);
        Assert.Null(error.HttpStatusCode);
        Assert.True(error.RequestWasSent);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task In_stream_error_after_partial_output_never_completes_or_replays()
    {
        var handler = new RecordingHandler(_ => StreamResponse(
            "data: {\"choices\":[{\"delta\":{\"content\":\"partial\"}}]}\n\n" + ErrorFrame(502) + "data: [DONE]\n\n"));
        var provider = CreateProvider(handler, "fixture");
        var chunks = new List<ChatStreamChunk>();
        var error = await Assert.ThrowsAsync<ProviderRequestException>(async () => {
            await foreach (var chunk in provider.StreamAsync(BuildToolRequest(), default)) chunks.Add(chunk);
        });
        Assert.Equal("partial", string.Concat(chunks.Select(c => c.TextDelta)));
        Assert.DoesNotContain(chunks, chunk => chunk.IsComplete);
        Assert.Equal(ProviderFailureCategory.ServiceUnavailable, error.Category);
        Assert.True(error.RequestWasSent);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Malformed_stream_after_http_response_is_marked_sent()
    {
        var handler = new RecordingHandler(_ => StreamResponse("data: {\"choices\":[]}\n\n"));
        var error = await Assert.ThrowsAsync<ProviderRequestException>(() => StreamAll(CreateProvider(handler, "fixture"), BuildToolRequest()));
        Assert.Equal(ProviderFailureCategory.InvalidResponse, error.Category);
        Assert.True(error.RequestWasSent);
        Assert.Equal(HttpStatusCode.OK, error.HttpStatusCode);
    }

    private static string ErrorFrame(int? status) => "data: " + JsonSerializer.Serialize(new {
        id = "synthetic", @object = "chat.completion.chunk", choices = Array.Empty<object>(),
        error = new { status, code = "upstream_error", message = "SYNTHETIC_PRIVATE_ERROR_DETAIL" }
    }) + "\n\n";
}
