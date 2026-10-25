using System.Net;
using System.IO;
using System.Net.Http.Json;
using System.Text;
using FgoPet.Core.Dialogue;
using FgoPet.Infrastructure.Providers;
using FgoPet.Infrastructure.Secrets;
using Xunit;

namespace FgoPet.Infrastructure.Tests.Providers;

public sealed partial class OpenAiCompatibleChatProviderTests
{
    [Theory]
    [InlineData("http://127.0.0.1:8326/v1", "buddy/deepseek-v4.1-flash", 0)]
    [InlineData("https://proxy.test/v1", "buddy/deepseek-v4.1-flash", 2)]
    [InlineData("http://127.0.0.1:8326/v1", "other-model", 2)]
    public async Task Local_buddy_indexes_are_scoped_to_each_response_and_keep_fragments_together(string endpoint, string model, int expected)
    {
        var handler = new RecordingHandler(_ => StreamResponse(
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":2,\"id\":\"a\",\"function\":{\"name\":\"first\",\"arguments\":\"{\"}}]}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":5,\"id\":\"b\",\"function\":{\"name\":\"second\",\"arguments\":\"{}\"}},{\"index\":2,\"function\":{\"arguments\":\"}\"}}]},\"finish_reason\":\"tool_calls\"}]}\n\ndata: [DONE]\n\n"));
        var provider = new OpenAiCompatibleChatProvider("custom-openai-compatible", new Uri(endpoint), model,
            new FakeCredentialReader("fixture"), new HttpClient(handler));
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var chunks = await StreamAll(provider, BuildToolRequest());
            var calls = chunks.Where(c => c.ToolCallDelta is not null).Select(c => c.ToolCallDelta!).ToArray();
            Assert.Equal(new[] { expected, expected == 0 ? 1 : 5, expected }, calls.Select(c => c.Index));
            Assert.Equal(new[] { "a", "b", null }, calls.Select(c => c.Id));
            Assert.Equal(new[] { "{", "{}", "}" }, calls.Select(c => c.ArgumentsDelta));
        }
    }

    [Fact]
    public async Task Local_buddy_rejects_excess_calls_without_merging_identities()
    {
        var body = string.Concat(Enumerable.Range(0, 17).Select(i =>
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":" + (i * 2) +
            ",\"id\":\"call" + i + "\",\"function\":{\"name\":\"first\",\"arguments\":\"{}\"}}]}}]}\n\n"));
        var provider = new OpenAiCompatibleChatProvider("custom-openai-compatible", new Uri("http://127.0.0.1:8326/v1"),
            "buddy/deepseek-v4.1-flash", new FakeCredentialReader("fixture"),
            new HttpClient(new RecordingHandler(_ => StreamResponse(body + "data: [DONE]\n\n"))));
        var error = await Assert.ThrowsAsync<ProviderRequestException>(() => StreamAll(provider, BuildToolRequest()));
        Assert.Equal(ProviderFailureCategory.InvalidResponse, error.Category);
    }

    [Fact]
    public async Task All_calls_in_a_single_delta_are_emitted_in_order()
    {
        var handler = new RecordingHandler(_ => StreamResponse(
            "data: {\"choices\":[{\"delta\":{\"content\":\"fixture\",\"tool_calls\":[{\"index\":0,\"id\":\"a\",\"function\":{\"name\":\"first\",\"arguments\":\"{}\"}},{\"index\":1,\"id\":\"b\",\"function\":{\"name\":\"second\",\"arguments\":\"{}\"}}]},\"finish_reason\":\"tool_calls\"}]}\n\ndata: [DONE]\n\n"));
        var chunks = await StreamAll(CreateProvider(handler, "fixture"), BuildToolRequest());
        Assert.Equal(new[] { "a", "b" }, chunks.Where(c => c.ToolCallDelta is not null).Select(c => c.ToolCallDelta!.Id));
        Assert.Equal("fixture", string.Concat(chunks.Select(c => c.TextDelta)));
    }

    [Fact]
    public async Task End_of_stream_without_done_is_never_complete()
    {
        var handler = new RecordingHandler(_ => StreamResponse(
            "data: {\"choices\":[{\"delta\":{\"content\":\"partial\"},\"finish_reason\":\"stop\"}]}\n\n"));
        var chunks = await StreamAll(CreateProvider(handler, "fixture"), BuildToolRequest());
        Assert.DoesNotContain(chunks, c => c.IsComplete);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Response_stream_io_is_reported_as_sent_transport_failure_without_replaying_or_fabricating_completion(bool afterDelta, bool failOpening)
    {
        var prefix = afterDelta ? "data: {\"choices\":[{\"delta\":{\"content\":\"synthetic partial\"}}]}\n\n" : "";
        var handler = new RecordingHandler(_ => new(HttpStatusCode.OK) {
            Content = failOpening ? new FailingResponseContent() : new StreamContent(new FailingResponseStream(prefix))
        });
        var provider = CreateProvider(handler, "synthetic-credential");
        var observed = new List<ChatStreamChunk>();
        var error = await Assert.ThrowsAsync<ProviderRequestException>(async () => {
            await foreach (var chunk in provider.StreamAsync(BuildToolRequest(), default)) observed.Add(chunk);
        });
        Assert.Equal(ProviderFailureCategory.Network, error.Category);
        Assert.True(error.RequestWasSent);
        Assert.IsType<IOException>(error.InnerException);
        Assert.DoesNotContain("SYNTHETIC_PRIVATE_TRANSPORT_DETAIL", error.Message);
        Assert.Equal(afterDelta ? "synthetic partial" : "", string.Concat(observed.Select(c => c.TextDelta)));
        Assert.DoesNotContain(observed, c => c.IsComplete);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_wins_over_an_io_error_while_reading_the_response(bool failOpening)
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new RecordingHandler(_ => new(HttpStatusCode.OK) {
            Content = failOpening ? new FailingResponseContent(cancellation.Cancel) : new StreamContent(new FailingResponseStream("", cancellation.Cancel))
        });
        var provider = CreateProvider(handler, "synthetic-credential");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => {
            await foreach (var chunk in provider.StreamAsync(BuildToolRequest(), cancellation.Token)) { }
        });
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("\"bad\"")]
    [InlineData("999999999999")]
    public async Task Invalid_call_indexes_are_rejected_before_emitting_calls(string index)
    {
        var handler = new RecordingHandler(_ => StreamResponse(
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":" + index + ",\"id\":\"a\",\"function\":{\"name\":\"first\",\"arguments\":\"{}\"}}]}}]}\n\ndata: [DONE]\n\n"));
        var error = await Assert.ThrowsAsync<ProviderRequestException>(() => StreamAll(CreateProvider(handler, "fixture"), BuildToolRequest()));
        Assert.Equal(ProviderFailureCategory.InvalidResponse, error.Category);
    }

    [Fact]
    public async Task Oversized_stream_lines_are_rejected_before_json_parsing()
    {
        var handler = new RecordingHandler(_ => StreamResponse("data: " + new string(' ', 600000) +
            "{\"choices\":[{\"delta\":{\"content\":\"fixture\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n"));
        var error = await Assert.ThrowsAsync<ProviderRequestException>(() => StreamAll(CreateProvider(handler, "fixture"), BuildToolRequest()));
        Assert.Equal(ProviderFailureCategory.InvalidResponse, error.Category);
    }

    [Theory]
    [InlineData("https://api.openai.com/v1", "max_completion_tokens")]
    [InlineData("https://proxy.test/v1", "max_tokens")]
    public async Task Output_field_matches_the_actual_endpoint(string endpoint, string field)
    {
        var handler = new RecordingHandler(_ => StreamResponse("data: [DONE]\n\n"));
        var provider = new OpenAiCompatibleChatProvider("openai", new Uri(endpoint), "m",
            new TestCredentialReader(), new HttpClient(handler));
        await ConsumeAsync(provider, new ChatRequest("mash", "c", [new(ChatMessageRole.User, "hello")], maxOutputTokens: 1234));
        Assert.Contains("\"" + field + "\":1234", handler.RequestLog);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("\"4096\"")]
    [InlineData("999999999999")]
    public async Task Invalid_output_metadata_invalidates_capacity_metadata(string value)
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StringContent("{\"data\":[{\"id\":\"m\",\"context_length\":32768,\"max_output_tokens\":" + value + "}]}") });
        var model = Assert.Single(await CreateProvider(handler, "secret").ListModelsAsync(default));
        Assert.Null(model.ContextWindowTokens);
    }

    private sealed class TestCredentialReader : ICredentialReader
    {
        public Task<string?> ReadAsync(string target, CancellationToken cancellationToken) => Task.FromResult<string?>("fixture");
    }

    [Fact]
    public async Task Selected_output_budget_is_actually_sent()
    {
        var handler = new RecordingHandler(_ => StreamResponse("data: [DONE]\n\n"));
        await ConsumeAsync(CreateProvider(handler, "secret"), new ChatRequest("mash", "c",
            [new(ChatMessageRole.User, "继续")], maxOutputTokens: 1234));
        Assert.Contains("\"max_tokens\":1234", handler.RequestLog);
    }

    [Fact]
    public async Task Usage_only_trailer_does_not_require_choices()
    {
        var handler = new RecordingHandler(_ => StreamResponse(
            "data: {\"choices\":[{\"delta\":{\"content\":\"你好\"},\"finish_reason\":\"stop\"}]}\n\n" +
            "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":123,\"completion_tokens\":7}}\n\n" +
            "data: [DONE]\n\n"));
        var chunks = await StreamAll(CreateProvider(handler, "secret"),
            new ChatRequest("mash", "c", [new(ChatMessageRole.User, "你好")]));
        var usage = Assert.Single(chunks.Where(chunk => chunk.Usage is not null)).Usage!;
        Assert.Equal(123, usage.InputTokens);
        Assert.Equal(7, usage.OutputTokens);
        Assert.Equal("你好", string.Concat(chunks.Select(chunk => chunk.TextDelta)));
    }

    [Fact]
    public async Task Model_discovery_reads_supported_capacity_metadata()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StringContent("""{"data":[{"id":"unknown","context_length":32768,"max_output_tokens":4096}]}"""),
        });
        var model = Assert.Single(await CreateProvider(handler, "secret").ListModelsAsync(default));
        Assert.Equal(32768, model.ContextWindowTokens);
        Assert.Equal(4096, model.MaxOutputTokens);
    }

    [Fact]
    public async Task Context_limit_error_is_not_misclassified_as_tools_rejection()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest) {
            Content = new StringContent("""{"error":{"code":"context_length_exceeded","message":"too long"}}"""),
        });
        var error = await Assert.ThrowsAsync<ProviderRequestException>(() =>
            ConsumeAsync(CreateProvider(handler, "secret"), BuildToolRequest()));
        Assert.NotEqual(ProviderFailureCategory.ToolsRejected, error.Category);
    }

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
            tools: [TodoToolContracts.CreateSubmitTodoProposals().ToChatDefinition()],
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
        tools: [TodoToolContracts.CreateSubmitTodoProposals().ToChatDefinition()],
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
        public int Calls { get; private set; }
        public string RequestLog { get; private set; } = string.Empty;
        public string? AuthorizationHeader { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            AuthorizationHeader = request.Headers.Authorization?.ToString();
            RequestLog = $"{request.Method} {request.RequestUri} {request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult()}";
            return Task.FromResult(responseFactory(request));
        }
    }

    private sealed class FailingResponseContent(Action? beforeFailure = null) : HttpContent
    {
        protected override Task<Stream> CreateContentReadStreamAsync()
        {
            beforeFailure?.Invoke();
            throw new IOException("SYNTHETIC_PRIVATE_TRANSPORT_DETAIL");
        }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new NotSupportedException();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    private sealed class FailingResponseStream(string prefix, Action? beforeFailure = null) : Stream
    {
        private readonly MemoryStream buffered = new(Encoding.UTF8.GetBytes(prefix));
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => buffered.Length;
        public override long Position { get => buffered.Position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            var count = buffered.Read(buffer);
            if (count > 0) return count;
            beforeFailure?.Invoke();
            throw new IOException("SYNTHETIC_PRIVATE_TRANSPORT_DETAIL");
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => ValueTask.FromResult(Read(buffer.Span));
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) buffered.Dispose(); base.Dispose(disposing); }
    }
}
