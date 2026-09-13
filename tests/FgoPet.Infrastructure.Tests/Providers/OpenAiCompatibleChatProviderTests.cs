using System.Net;
using System.Net.Http.Json;
using System.Text;
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

    [Fact]
    public async Task Tools_are_sent_with_the_request_and_auto_tool_choice()
    {
        var handler = new RecordingHandler(_ => StreamResponse("data: [DONE]\n\n"));
        var provider = CreateProvider(handler, "secret-value");
        var request = new ChatRequest(
            "800100",
            "conversation-1",
            new[] { new PromptMessage(ChatMessageRole.User, "你好") },
            tools: [TodoToolContracts.CreateSubmitTodoProposals()],
            toolChoice: "auto");

        await foreach (var _ in provider.StreamAsync(request, CancellationToken.None))
        {
        }

        Assert.Contains("\"tools\"", handler.RequestLog, StringComparison.Ordinal);
        Assert.Contains("submit_todo_proposals", handler.RequestLog, StringComparison.Ordinal);
        Assert.Contains("\"tool_choice\":\"auto\"", handler.RequestLog, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Omitted_tools_never_add_tool_fields_to_the_request()
    {
        var handler = new RecordingHandler(_ => StreamResponse("data: [DONE]\n\n"));
        var provider = CreateProvider(handler, "secret-value");
        var request = new ChatRequest(
            "800100",
            "conversation-1",
            new[] { new PromptMessage(ChatMessageRole.User, "你好") });

        await foreach (var _ in provider.StreamAsync(request, CancellationToken.None))
        {
        }

        Assert.DoesNotContain("\"tools\"", handler.RequestLog, StringComparison.Ordinal);
        Assert.DoesNotContain("tool_choice", handler.RequestLog, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tool_call_fragments_stream_as_deltas_and_aggregate_into_one_call()
    {
        var handler = new RecordingHandler(_ => StreamResponse(
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call-1\",\"function\":{\"name\":\"submit_todo_proposals\",\"arguments\":\"\"}}]}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"{\\\"todos\\\":\"}}]}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n\n" +
            "data: [DONE]\n\n"));
        var provider = CreateProvider(handler, "secret-value");

        var chunks = await StreamAll(provider, BuildToolRequest());

        Assert.Equal(2, chunks.Count(chunk => chunk.ToolCallDelta is not null));
        Assert.Equal("submit_todo_proposals", chunks[0].ToolCallDelta!.Name);
        Assert.Equal("{\"todos\":", chunks[1].ToolCallDelta!.ArgumentsDelta);
        Assert.Equal("tool_calls", chunks.Last(chunk => chunk.FinishReason is not null).FinishReason);
        Assert.True(chunks[^1].IsComplete);
    }

    [Fact]
    public async Task Tool_call_without_index_defaults_to_zero()
    {
        var handler = new RecordingHandler(_ => StreamResponse(
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"function\":{\"arguments\":\"{}\"}}]}}]}\n\n" +
            "data: [DONE]\n\n"));
        var provider = CreateProvider(handler, "secret-value");

        var chunks = await StreamAll(provider, BuildToolRequest());

        var delta = Assert.Single(chunks.Where(chunk => chunk.ToolCallDelta is not null)).ToolCallDelta!;
        Assert.Equal(0, delta.Index);
        Assert.Equal("{}", delta.ArgumentsDelta);
    }

    [Fact]
    public async Task Terminal_done_chunk_repeats_the_last_finish_reason()
    {
        var handler = new RecordingHandler(_ => StreamResponse(
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n\n" +
            "data: [DONE]\n\n"));
        var provider = CreateProvider(handler, "secret-value");

        var chunks = await StreamAll(provider, BuildToolRequest());

        Assert.True(chunks[^1].IsComplete);
        Assert.Equal("tool_calls", chunks[^1].FinishReason);
    }

    [Fact]
    public async Task Streaming_without_explicit_finish_reason_reports_none_at_the_end()
    {
        var handler = new RecordingHandler(_ => StreamResponse(
            "data: {\"choices\":[{\"delta\":{\"content\":\"你好\"}}]}\n\n" +
            "data: [DONE]\n\n"));
        var provider = CreateProvider(handler, "secret-value");
        var request = new ChatRequest(
            "800100",
            "conversation-1",
            new[] { new PromptMessage(ChatMessageRole.User, "你好") });

        var chunks = await StreamAll(provider, request);

        Assert.True(chunks[^1].IsComplete);
        Assert.Null(chunks[^1].FinishReason);
    }

    [Fact]
    public async Task Empty_choices_are_reported_as_invalid_stream_data()
    {
        var handler = new RecordingHandler(_ => StreamResponse(
            "data: {\"choices\":[]}\n\n"));
        var provider = CreateProvider(handler, "secret-value");

        var error = await Assert.ThrowsAsync<ProviderRequestException>(
            () => ConsumeAsync(provider, new ChatRequest(
                "800100",
                "conversation-1",
                new[] { new PromptMessage(ChatMessageRole.User, "你好") })));

        Assert.Equal(ProviderFailureCategory.InvalidResponse, error.Category);
    }

    [Fact]
    public async Task Bad_request_with_tools_raises_tools_rejected()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"error\":{\"message\":\"unknown parameter tools\"}}"),
        });
        var provider = CreateProvider(handler, "secret-value");

        var error = await Assert.ThrowsAsync<ProviderRequestException>(
            () => ConsumeAsync(provider, BuildToolRequest()));

        Assert.Equal(ProviderFailureCategory.ToolsRejected, error.Category);
    }

    [Fact]
    public async Task Bad_request_without_tools_keeps_the_normal_failure_category()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"error\":{\"message\":\"bad messages\"}}"),
        });
        var provider = CreateProvider(handler, "secret-value");
        var request = new ChatRequest(
            "800100",
            "conversation-1",
            new[] { new PromptMessage(ChatMessageRole.User, "你好") });

        var error = await Assert.ThrowsAsync<ProviderRequestException>(
            () => ConsumeAsync(provider, request));

        Assert.NotEqual(ProviderFailureCategory.ToolsRejected, error.Category);
    }

    [Fact]
    public async Task Provider_http_error_preserves_status_and_safe_provider_message()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"error\":{\"code\":\"invalid_request\",\"message\":\"bad messages\"}}"),
        });
        var provider = CreateProvider(handler, "secret-value");

        var error = await Assert.ThrowsAsync<ProviderRequestException>(
            () => ConsumeAsync(provider, new ChatRequest(
                "800100",
                "conversation-1",
                new[] { new PromptMessage(ChatMessageRole.User, "你好") })));

        Assert.Equal(HttpStatusCode.BadRequest, error.HttpStatusCode);
        Assert.Equal("invalid_request", error.ProviderCode);
        Assert.Contains("bad messages", error.Message, StringComparison.Ordinal);
        Assert.Contains("400", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reasoning_content_deltas_stream_as_reasoning_and_never_as_text()
    {
        var handler = new RecordingHandler(_ => StreamResponse(
            "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"用户想要待办\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"好的\"}}]}\n\n" +
            "data: [DONE]\n\n"));
        var provider = CreateProvider(handler, "secret-value");
        var request = new ChatRequest(
            "800100",
            "conversation-1",
            new[] { new PromptMessage(ChatMessageRole.User, "你好") });

        var chunks = await StreamAll(provider, request);

        Assert.Equal("用户想要待办", chunks[0].ReasoningDelta);
        Assert.Equal(string.Empty, chunks[0].TextDelta);
        Assert.Equal("好的", chunks[1].TextDelta);
    }

    [Fact]
    public async Task Reasoning_field_deltas_stream_as_reasoning_too()
    {
        var handler = new RecordingHandler(_ => StreamResponse(
            "data: {\"choices\":[{\"delta\":{\"reasoning\":\"先想想\"}}]}\n\n" +
            "data: [DONE]\n\n"));
        var provider = CreateProvider(handler, "secret-value");
        var request = new ChatRequest(
            "800100",
            "conversation-1",
            new[] { new PromptMessage(ChatMessageRole.User, "你好") });

        var chunks = await StreamAll(provider, request);

        Assert.Equal("先想想", Assert.Single(chunks.Where(chunk => chunk.ReasoningDelta is not null)).ReasoningDelta);
    }

    [Fact]
    public async Task Reasoning_deltas_keep_leading_spaces_between_english_words()
    {
        var handler = new RecordingHandler(_ => StreamResponse(
            "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"The\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\" model\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\" is\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\" reasoning\"}}]}\n\n" +
            "data: [DONE]\n\n"));
        var provider = CreateProvider(handler, "secret-value");
        var request = new ChatRequest(
            "800100",
            "conversation-1",
            new[] { new PromptMessage(ChatMessageRole.User, "hello") });

        var chunks = await StreamAll(provider, request);

        var reasoning = string.Concat(chunks.Select(chunk => chunk.ReasoningDelta));
        Assert.Equal("The model is reasoning", reasoning);
    }

    [Fact]
    public async Task A_delta_carrying_both_reasoning_and_content_surfaces_both()
    {
        var handler = new RecordingHandler(_ => StreamResponse(
            "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"先想\",\"content\":\"答案\"}}]}\n\n" +
            "data: [DONE]\n\n"));
        var provider = CreateProvider(handler, "secret-value");
        var request = new ChatRequest(
            "800100",
            "conversation-1",
            new[] { new PromptMessage(ChatMessageRole.User, "你好") });

        var chunks = await StreamAll(provider, request);

        Assert.Equal("先想", string.Concat(chunks.Select(chunk => chunk.ReasoningDelta)));
        Assert.Equal("答案", string.Concat(chunks.Select(chunk => chunk.TextDelta)));
    }

    [Fact]
    public async Task Streams_without_reasoning_fields_behave_exactly_as_before()
    {
        var handler = new RecordingHandler(_ => StreamResponse(
            "data: {\"choices\":[{\"delta\":{\"content\":\"你好\"}}]}\n\n" +
            "data: [DONE]\n\n"));
        var provider = CreateProvider(handler, "secret-value");
        var request = new ChatRequest(
            "800100",
            "conversation-1",
            new[] { new PromptMessage(ChatMessageRole.User, "你好") });

        var chunks = await StreamAll(provider, request);

        Assert.Equal("你好", string.Concat(chunks.Select(chunk => chunk.TextDelta)));
        Assert.All(chunks, chunk => Assert.Null(chunk.ReasoningDelta));
        Assert.All(chunks, chunk => Assert.Null(chunk.ToolCallDelta));
    }

    private static ChatRequest BuildToolRequest() => new(
        "800100",
        "conversation-1",
        new[] { new PromptMessage(ChatMessageRole.User, "你好") },
        tools: [TodoToolContracts.CreateSubmitTodoProposals()],
        toolChoice: "auto");

    private static HttpResponseMessage StreamResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
    };

    private static async Task<IReadOnlyList<ChatStreamChunk>> StreamAll(
        OpenAiCompatibleChatProvider provider,
        ChatRequest request)
    {
        var chunks = new List<ChatStreamChunk>();
        await foreach (var chunk in provider.StreamAsync(request, CancellationToken.None))
        {
            chunks.Add(chunk);
        }

        return chunks;
    }

    private static async Task ConsumeAsync(OpenAiCompatibleChatProvider provider, ChatRequest request)
    {
        await foreach (var _ in provider.StreamAsync(request, CancellationToken.None))
        {
        }
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
