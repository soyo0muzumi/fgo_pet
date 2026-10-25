using System.IO;
using System.Net.Http;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Settings;
using FgoPet.Infrastructure.Secrets;
using FgoPet.Kernel.Agent;

namespace FgoPet.Infrastructure.Providers;

public sealed class OpenAiCompatibleChatProvider : IChatProvider, IChatImageInputProvider
{
    private readonly Uri _baseUri;
    private readonly ICredentialReader _credentialReader;
    private readonly HttpClient _httpClient;
    private readonly IChatDispatchGate? _dispatchGate;
    private readonly ModelRouteKey _route;
    private readonly IChatImageResolver? _images;

    public OpenAiCompatibleChatProvider(
        string providerId,
        Uri baseUri,
        string modelId,
        ICredentialReader credentialReader,
        HttpClient httpClient,
        IChatDispatchGate? dispatchGate = null,
        ModelRouteKey? route = null,
        IChatImageResolver? images = null)
    {
        if (baseUri is null || !baseUri.IsAbsoluteUri || baseUri.Scheme is not ("https" or "http"))
        {
            throw new ArgumentException("Provider base URL must be an absolute HTTP(S) URI.", nameof(baseUri));
        }

        if (baseUri.Scheme == Uri.UriSchemeHttp && !baseUri.IsLoopback)
        {
            throw new ArgumentException("Non-loopback provider endpoints must use HTTPS.", nameof(baseUri));
        }

        ProviderId = string.IsNullOrWhiteSpace(providerId) ? throw new ArgumentException("Provider ID is required.", nameof(providerId)) : providerId.Trim();
        ModelId = string.IsNullOrWhiteSpace(modelId) ? throw new ArgumentException("Model ID is required.", nameof(modelId)) : modelId.Trim();
        _baseUri = new Uri(baseUri.ToString().TrimEnd('/') + "/", UriKind.Absolute);
        _credentialReader = credentialReader ?? throw new ArgumentNullException(nameof(credentialReader));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _dispatchGate = dispatchGate;
        var physicalRoute=ModelRouteKey.From(new ModelConnectionSettings(ProviderId,_baseUri.AbsoluteUri,ModelId));
        if(route is not null&&(route.ProviderId!=physicalRoute.ProviderId||route.ModelId!=physicalRoute.ModelId||route.EndpointKey!=physicalRoute.EndpointKey))
            throw new ArgumentException("MODEL_DISPATCH_ROUTE_MISMATCH",nameof(route));
        _route=route??physicalRoute;
        _images = images;
        CredentialTarget = $"fgo-pet/provider/{ProviderId}";
    }

    public string ProviderId { get; }

    public string ModelId { get; }

    public string CredentialTarget { get; }

    public void PrepareImages(ChatRequest request, CancellationToken token) =>
        ChatImageInputs.Prepare(request, _route, _images, ImageModelCapabilities.Supports(_route), token);

    public async Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken cancellationToken)
    {
        using var request = await CreateAuthorizedRequestAsync(HttpMethod.Get, "models", cancellationToken);
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException error)
        {
            throw new ProviderRequestException(ProviderFailureCategory.Network, "无法连接模型服务。", error);
        }

        using (response)
        {
            await EnsureSuccessAsync(response, cancellationToken);
            try
            {
                using var document = await JsonDocument.ParseAsync(
                    await response.Content.ReadAsStreamAsync(cancellationToken),
                    cancellationToken: cancellationToken);
                if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                {
                    throw new ProviderRequestException(ProviderFailureCategory.InvalidResponse, "模型服务返回了无法识别的模型列表。");
                }

                return data.EnumerateArray()
                    .Select(item => item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                        ? ReadModel(item, id.GetString()!)
                        : null)
                    .OfType<ProviderModel>()
                    .ToArray();
            }
            catch (JsonException error)
            {
                throw new ProviderRequestException(ProviderFailureCategory.InvalidResponse, "模型服务返回了无法识别的模型列表。", error);
            }
        }
    }

    public async IAsyncEnumerable<ChatStreamChunk> StreamAsync(
        ChatRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        PrepareImages(request, cancellationToken);
        var ordinaryNames = !request.IsAgentRequest && request.Tools is { Count: > 0 }
            ? new ModelToolNameMap(request.Tools.Select(tool => tool.Name)) : null;
        using var httpRequest = await CreateAuthorizedRequestAsync(HttpMethod.Post, "chat/completions", cancellationToken);
        var route = _route;
        httpRequest.Content = new StringContent(
            ChatRequestPayloadWriter.Write(route, request),
            Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            // Credentials and serialization may await or execute host callbacks. Admission belongs
            // at this final transport boundary, with no await between its lease and SendAsync.
            Task<HttpResponseMessage> Send()
            {
                return ChatImageInputs.Dispatch(request, route, _images,
                    () => _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken), cancellationToken);
            }
            var pending = _dispatchGate is null ? Send()
                : _dispatchGate.Dispatch(request, route, Send, cancellationToken);
            response = await pending;
        }
        catch (HttpRequestException error)
        {
            throw new ProviderRequestException(ProviderFailureCategory.Network, "无法连接模型服务。", error);
        }

        using (response)
        {
            if (request.Tools is { Count: > 0 } && response.StatusCode == HttpStatusCode.BadRequest)
            {
                // The tools parameter itself is the plausible rejection cause only
                // when tools were attached; the retry without tools confirms it.
                throw await CreateHttpExceptionAsync(
                    response,
                    ProviderFailureCategory.ToolsRejected,
                    "当前模型服务不支持工具调用。",
                    cancellationToken);
            }

            await EnsureSuccessAsync(response, cancellationToken);
            await using var stream = await OpenResponseStreamAsync(response.Content, cancellationToken);
            using var reader = new StreamReader(stream);
            string? lastFinishReason = null;
            // The local Buddy bridge emits nonzero call indexes (observed single calls
            // at indexes 1 and 2). Treat those numbers as fragment keys,
            // scoped to this response. Keep strict Native identity/argument validation.
            Dictionary<int, int>? buddyIndexes = _baseUri.IsLoopback && ModelId == "buddy/deepseek-v4.1-flash"
                ? new() : null;
            await foreach (var line in ReadLinesBoundedAsync(reader, cancellationToken))
            {
                if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var data = line["data:".Length..].Trim();
                if (data.Equals("[DONE]", StringComparison.Ordinal))
                {
                    // The terminal chunk must repeat the last observed finish reason:
                    // a literal "stop" here would clobber a real "tool_calls" marker
                    // that arrived earlier in the stream.
                    yield return new ChatStreamChunk(string.Empty, IsComplete: true, FinishReason: lastFinishReason);
                    break;
                }

                string? finishReason = null;
                IReadOnlyList<ChatToolCallDelta> toolCallDeltas = [];
                string? textDelta = null;
                string? reasoningDelta = null;
                ChatUsage? usage = null;
                try
                {
                    using var document = JsonDocument.Parse(data);
                    var root = document.RootElement;
                    if (root.TryGetProperty("error", out var streamError) && streamError.ValueKind == JsonValueKind.Object)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        throw ReadStreamError(streamError);
                    }
                    if (root.TryGetProperty("usage", out var usageElement) && usageElement.ValueKind == JsonValueKind.Object)
                    {
                        var input = ReadNonnegativeInt(usageElement, "prompt_tokens");
                        var output = ReadNonnegativeInt(usageElement, "completion_tokens");
                        if (input is int inputTokens && output is int outputTokens)
                            usage = new ChatUsage(inputTokens, outputTokens);
                    }
                    var hasChoices = root.TryGetProperty("choices", out var choices)
                        && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0;
                    if (!hasChoices && usage is null)
                    {
                        throw new JsonException("Streaming response contains no choices.");
                    }
                    if (hasChoices)
                    {
                    var choice = choices[0];
                    finishReason = choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String
                        ? finish.GetString()
                        : null;
                    if (choice.TryGetProperty("delta", out var delta))
                    {
                        textDelta = delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String
                            ? content.GetString()
                            : null;
                        reasoningDelta = ReadReasoningDelta(delta);
                        toolCallDeltas = ReadToolCallDeltas(delta);
                        if (ordinaryNames is not null)
                            toolCallDeltas = toolCallDeltas.Select(call => call.Name is { } alias && ordinaryNames.TryResolve(alias, out var canonical)
                                ? new ChatToolCallDelta(call.Index, call.Id, canonical, call.ArgumentsDelta) : call).ToArray();
                        if (buddyIndexes is not null)
                        {
                            toolCallDeltas = toolCallDeltas.Select(call =>
                            {
                                if (!buddyIndexes.TryGetValue(call.Index, out var normalized))
                                {
                                    if (buddyIndexes.Count >= FgoPet.Kernel.Agent.ModelProtocol.MaxCalls)
                                        throw new JsonException("Too many calls.");
                                    buddyIndexes.Add(call.Index, normalized = buddyIndexes.Count);
                                }
                                return new ChatToolCallDelta(normalized, call.Id, call.Name, call.ArgumentsDelta);
                            }).ToArray();
                        }
                    }
                    else
                    {
                        textDelta = null;
                        reasoningDelta = null;
                    }
                    }
                }
                catch (JsonException error)
                {
                    throw new ProviderRequestException(ProviderFailureCategory.InvalidResponse, "模型服务返回了无法识别的串流数据。", error,
                        httpStatusCode: response.StatusCode, requestWasSent: true);
                }
                catch (Exception error) when (error is InvalidOperationException or IndexOutOfRangeException or ArgumentException or KeyNotFoundException)
                {
                    throw new ProviderRequestException(ProviderFailureCategory.InvalidResponse, "模型服务返回了无法识别的串流数据。", error,
                        httpStatusCode: response.StatusCode, requestWasSent: true);
                }

                if (finishReason is not null)
                {
                    lastFinishReason = finishReason;
                }

                // A single delta may carry content, reasoning, tool calls or a finish
                // reason together; emitting the first match only would drop the rest.
                if (!string.IsNullOrEmpty(textDelta) || toolCallDeltas.Count > 0
                    || reasoningDelta is not null || finishReason is not null || usage is not null)
                {
                    yield return new ChatStreamChunk(
                        textDelta ?? string.Empty,
                        IsComplete: false,
                        FinishReason: finishReason,
                        ToolCallDelta: toolCallDeltas.FirstOrDefault(),
                        ReasoningDelta: reasoningDelta,
                        Usage: usage);
                    foreach (var call in toolCallDeltas.Skip(1))
                        yield return new ChatStreamChunk(string.Empty, ToolCallDelta: call);
                }
            }

            // EOF is not the provider's completion marker. Native aggregation rejects a truncated stream.
        }
    }

    private static async IAsyncEnumerable<string> ReadLinesBoundedAsync(StreamReader reader,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        // Includes JSON escaping headroom for all allowed deltas, with a finite pre-parse allocation bound.
        const int maxLineCharacters = 512 * 1024;
        var buffer = new char[4096];
        var line = new StringBuilder();
        int count;
        while ((count = await ReadResponseBufferAsync(reader, buffer.AsMemory(), token)) > 0)
            for (var index = 0; index < count; index++)
            {
                var character = buffer[index];
                if (character is '\r' or '\n')
                {
                    yield return line.ToString();
                    line.Clear();
                    continue;
                }
                if (line.Length >= maxLineCharacters)
                    throw new ProviderRequestException(ProviderFailureCategory.InvalidResponse, "模型串流数据超出安全上限。", requestWasSent: true);
                line.Append(character);
            }
        if (line.Length > 0) yield return line.ToString();
    }

    private static async Task<Stream> OpenResponseStreamAsync(HttpContent content, CancellationToken token)
    {
        try { return await content.ReadAsStreamAsync(token); }
        catch (IOException error)
        {
            token.ThrowIfCancellationRequested();
            throw new ProviderRequestException(ProviderFailureCategory.Network, "模型响应流中断，请稍后重试。", error,
                requestWasSent: true);
        }
    }

    private static async ValueTask<int> ReadResponseBufferAsync(StreamReader reader, Memory<char> buffer, CancellationToken token)
    {
        try { return await reader.ReadAsync(buffer, token); }
        catch (IOException error)
        {
            token.ThrowIfCancellationRequested();
            throw new ProviderRequestException(ProviderFailureCategory.Network, "模型响应流中断，请稍后重试。", error,
                requestWasSent: true);
        }
    }

    private static int? ReadNonnegativeInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var number) && number >= 0 ? number : null;

    private static ProviderModel ReadModel(JsonElement item, string id)
    {
        foreach (var field in new[] { "context_window", "context_length", "max_output_tokens" })
            if (item.TryGetProperty(field, out var value) && value.ValueKind != JsonValueKind.Null &&
                ReadNonnegativeInt(item, field) is not > 0)
                return new ProviderModel(id);
        var window = ReadNonnegativeInt(item, "context_window");
        var length = ReadNonnegativeInt(item, "context_length");
        var maximum = ReadNonnegativeInt(item, "max_output_tokens");
        var capacity = window ?? length;
        if (window is not null && length is not null && window != length ||
            capacity is <= 0 || maximum is <= 0 || maximum > capacity)
            return new ProviderModel(id);
        return new ProviderModel(id, contextWindowTokens: capacity, maxOutputTokens: maximum);
    }

    private static string? ReadReasoningDelta(JsonElement delta)
    {
        // OpenAI-compatible reasoning fields vary by service: DeepSeek uses
        // "reasoning_content", others use "reasoning". Unknown services without
        // either field behave exactly as before.
        foreach (var propertyName in (string[])["reasoning_content", "reasoning"])
        {
            if (delta.TryGetProperty(propertyName, out var reasoning) && reasoning.ValueKind == JsonValueKind.String)
            {
                return reasoning.GetString();
            }
        }

        return null;
    }

    private static IReadOnlyList<ChatToolCallDelta> ReadToolCallDeltas(JsonElement delta)
    {
        if (!delta.TryGetProperty("tool_calls", out var toolCalls) || toolCalls.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var calls = new List<ChatToolCallDelta>();
        if (toolCalls.GetArrayLength() > FgoPet.Kernel.Agent.ModelProtocol.MaxCalls) throw new JsonException("Too many calls.");
        foreach (var call in toolCalls.EnumerateArray())
        {
            var index = 0;
            if (call.TryGetProperty("index", out var indexElement))
            {
                if (indexElement.ValueKind != JsonValueKind.Number || !indexElement.TryGetInt32(out index) || index < 0)
                    throw new JsonException("Invalid call index.");
            }
            else if (toolCalls.GetArrayLength() > 1) throw new JsonException("Multiple calls require indexes.");
            string? id = call.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
                ? idElement.GetString()
                : null;
            string? name = null;
            string? argumentsDelta = null;
            if (call.TryGetProperty("function", out var function) && function.ValueKind == JsonValueKind.Object)
            {
                name = function.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                    ? nameElement.GetString()
                    : null;
                argumentsDelta = function.TryGetProperty("arguments", out var argumentsElement) && argumentsElement.ValueKind == JsonValueKind.String
                    ? argumentsElement.GetString()
                    : null;
            }

            if (id is null && name is null && string.IsNullOrEmpty(argumentsDelta))
            {
                continue;
            }

            calls.Add(new ChatToolCallDelta(index, id, name, argumentsDelta));
        }

        return calls;
    }

    private async Task<HttpRequestMessage> CreateAuthorizedRequestAsync(
        HttpMethod method,
        string relativePath,
        CancellationToken cancellationToken)
    {
        var secret = await _credentialReader.ReadAsync(CredentialTarget, cancellationToken);
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new ProviderRequestException(ProviderFailureCategory.Configuration, "尚未配置模型 API Key。");
        }

        var request = new HttpRequestMessage(method, new Uri(_baseUri, relativePath));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return request;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var category = FailureCategory(response.StatusCode);
        throw await CreateHttpExceptionAsync(
            response,
            category,
            category switch
            {
                ProviderFailureCategory.Authentication => "模型服务认证失败。",
                ProviderFailureCategory.RateLimited => "模型服务请求过于频繁。",
                ProviderFailureCategory.ServiceUnavailable => "模型服务暂时不可用。",
                _ => "模型服务请求失败。",
            },
            cancellationToken);
    }

    private static ProviderFailureCategory FailureCategory(HttpStatusCode? status) => status switch
        {
            null => ProviderFailureCategory.ServiceUnavailable,
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ProviderFailureCategory.Authentication,
            HttpStatusCode.TooManyRequests => ProviderFailureCategory.RateLimited,
            >= HttpStatusCode.InternalServerError => ProviderFailureCategory.ServiceUnavailable,
            _ => ProviderFailureCategory.InvalidResponse,
        };

    private static ProviderRequestException ReadStreamError(JsonElement error)
    {
        HttpStatusCode? status = error.TryGetProperty("status", out var reported) && reported.ValueKind == JsonValueKind.Number &&
            reported.TryGetInt32(out var value) && value is >= 400 and <= 599 ? (HttpStatusCode)value : null;
        string? providerCode = error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String &&
            code.GetString() is { Length: > 0 and <= 96 } text && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.') ? text : null;
        // The HTTP stream was accepted; the optional status here is the provider's in-band
        // error status. Never expose its raw message or turn a later [DONE] into success.
        return new(FailureCategory(status), "模型服务返回了串流错误。", httpStatusCode: status,
            providerCode: providerCode, requestWasSent: true);
    }

    private static async Task<ProviderRequestException> CreateHttpExceptionAsync(
        HttpResponseMessage response,
        ProviderFailureCategory category,
        string fallback,
        CancellationToken cancellationToken)
    {
        string? providerCode = null;
        string? providerMessage = null;
        string? parameter = null;
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(body))
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.TryGetProperty("error", out var error))
                {
                    parameter = error.TryGetProperty("param", out var param) && param.ValueKind == JsonValueKind.String ? param.GetString() : null;
                    providerCode = error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String
                        ? code.GetString()
                        : null;
                    providerMessage = error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String
                        ? message.GetString()
                        : null;
                }
            }
        }
        catch (JsonException)
        {
            // Keep the safe category/status when a provider error body is malformed.
        }

        var detail = string.IsNullOrWhiteSpace(providerMessage) ? fallback : providerMessage.Trim();
        if (providerCode is "context_length_exceeded" or "context_window_exceeded")
            category = ProviderFailureCategory.ContextLimitExceeded;
        if (parameter is "max_tokens" or "max_completion_tokens")
        {
            category = ProviderFailureCategory.Configuration;
            fallback = "当前服务不接受输出上限设置，请检查模型连接配置。";
            detail = fallback;
        }
        var messageText = $"{fallback}（HTTP {(int)response.StatusCode}）：{detail}";
        return new ProviderRequestException(category, messageText, httpStatusCode: response.StatusCode, providerCode: providerCode, requestWasSent: true);
    }
}
