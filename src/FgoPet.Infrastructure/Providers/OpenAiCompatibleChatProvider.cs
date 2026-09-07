using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FgoPet.Core.Dialogue;
using FgoPet.Infrastructure.Secrets;

namespace FgoPet.Infrastructure.Providers;

public enum ProviderFailureCategory
{
    Configuration,
    Authentication,
    RateLimited,
    Network,
    ServiceUnavailable,
    InvalidResponse,
    ToolsRejected,
}

public sealed class ProviderRequestException : Exception
{
    public ProviderRequestException(
        ProviderFailureCategory category,
        string message,
        Exception? innerException = null,
        HttpStatusCode? httpStatusCode = null,
        string? providerCode = null,
        bool requestWasSent = false)
        : base(message, innerException)
    {
        Category = category;
        HttpStatusCode = httpStatusCode;
        ProviderCode = providerCode;
        RequestWasSent = requestWasSent;
    }

    public ProviderFailureCategory Category { get; }
    public HttpStatusCode? HttpStatusCode { get; }
    public string? ProviderCode { get; }
    public bool RequestWasSent { get; }
}

public sealed class OpenAiCompatibleChatProvider : IChatProvider
{
    private readonly Uri _baseUri;
    private readonly ICredentialReader _credentialReader;
    private readonly HttpClient _httpClient;

    public OpenAiCompatibleChatProvider(
        string providerId,
        Uri baseUri,
        string modelId,
        ICredentialReader credentialReader,
        HttpClient httpClient)
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
        CredentialTarget = $"fgo-pet/provider/{ProviderId}";
    }

    public string ProviderId { get; }

    public string ModelId { get; }

    public string CredentialTarget { get; }

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
                        ? new ProviderModel(id.GetString()!)
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
        using var httpRequest = await CreateAuthorizedRequestAsync(HttpMethod.Post, "chat/completions", cancellationToken);
        var payload = new Dictionary<string, object?>
        {
            ["model"] = ModelId,
            ["stream"] = true,
            ["messages"] = request.Messages.Select(message => new
            {
                role = message.Role.ToString().ToLowerInvariant(),
                content = message.Text,
            }),
        };
        if (request.Tools is { Count: > 0 })
        {
            payload["tools"] = request.Tools.Select(tool => new
            {
                type = tool.Type,
                function = new
                {
                    name = tool.Name,
                    description = tool.Description,
                    parameters = JsonSerializer.SerializeToElement(tool.Parameters),
                },
            });
            payload["tool_choice"] = request.ToolChoice switch
            {
                null or "auto" => "auto",
                _ => request.ToolChoice,
            };
        }
        httpRequest.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
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
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);
            var done = false;
            string? lastFinishReason = null;
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
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
                    done = true;
                    break;
                }

                string? finishReason = null;
                ChatToolCallDelta? toolCallDelta = null;
                string? textDelta;
                string? reasoningDelta;
                try
                {
                    using var document = JsonDocument.Parse(data);
                    var root = document.RootElement;
                    if (!root.TryGetProperty("choices", out var choices)
                        || choices.ValueKind != JsonValueKind.Array
                        || choices.GetArrayLength() == 0)
                    {
                        throw new JsonException("Streaming response contains no choices.");
                    }

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
                        toolCallDelta = ReadToolCallDelta(delta);
                    }
                    else
                    {
                        textDelta = null;
                        reasoningDelta = null;
                    }
                }
                catch (JsonException error)
                {
                    throw new ProviderRequestException(ProviderFailureCategory.InvalidResponse, "模型服务返回了无法识别的串流数据。", error);
                }
                catch (Exception error) when (error is InvalidOperationException or IndexOutOfRangeException or ArgumentException or KeyNotFoundException)
                {
                    throw new ProviderRequestException(ProviderFailureCategory.InvalidResponse, "模型服务返回了无法识别的串流数据。", error);
                }

                if (finishReason is not null)
                {
                    lastFinishReason = finishReason;
                }

                if (!string.IsNullOrEmpty(textDelta))
                {
                    yield return new ChatStreamChunk(textDelta, FinishReason: finishReason);
                    continue;
                }

                if (toolCallDelta is not null || reasoningDelta is not null || finishReason is not null)
                {
                    yield return new ChatStreamChunk(
                        string.Empty,
                        IsComplete: false,
                        FinishReason: finishReason,
                        ToolCallDelta: toolCallDelta,
                        ReasoningDelta: reasoningDelta);
                }
            }

            if (!done)
            {
                yield return new ChatStreamChunk(string.Empty, IsComplete: true);
            }
        }
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

    private static ChatToolCallDelta? ReadToolCallDelta(JsonElement delta)
    {
        if (!delta.TryGetProperty("tool_calls", out var toolCalls) || toolCalls.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var call in toolCalls.EnumerateArray())
        {
            var index = call.TryGetProperty("index", out var indexElement) && indexElement.TryGetInt32(out var parsed)
                ? parsed
                : 0;
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

            return new ChatToolCallDelta(index, id, name, argumentsDelta);
        }

        return null;
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

        var category = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ProviderFailureCategory.Authentication,
            HttpStatusCode.TooManyRequests => ProviderFailureCategory.RateLimited,
            >= HttpStatusCode.InternalServerError => ProviderFailureCategory.ServiceUnavailable,
            _ => ProviderFailureCategory.InvalidResponse,
        };
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

    private static async Task<ProviderRequestException> CreateHttpExceptionAsync(
        HttpResponseMessage response,
        ProviderFailureCategory category,
        string fallback,
        CancellationToken cancellationToken)
    {
        string? providerCode = null;
        string? providerMessage = null;
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(body))
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.TryGetProperty("error", out var error))
                {
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
        var messageText = $"{fallback}（HTTP {(int)response.StatusCode}）：{detail}";
        return new ProviderRequestException(category, messageText, httpStatusCode: response.StatusCode, providerCode: providerCode, requestWasSent: true);
    }
}
