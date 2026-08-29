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
}

public sealed class ProviderRequestException : Exception
{
    public ProviderRequestException(ProviderFailureCategory category, string message, Exception? innerException = null)
        : base(message, innerException) => Category = category;

    public ProviderFailureCategory Category { get; }
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
            EnsureSuccess(response);
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
        var payload = new
        {
            model = ModelId,
            stream = true,
            messages = request.Messages.Select(message => new
            {
                role = message.Role.ToString().ToLowerInvariant(),
                content = message.Text,
            }),
        };
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
            EnsureSuccess(response);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);
            var done = false;
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var data = line["data:".Length..].Trim();
                if (data.Equals("[DONE]", StringComparison.Ordinal))
                {
                    yield return new ChatStreamChunk(string.Empty, IsComplete: true, FinishReason: "stop");
                    done = true;
                    break;
                }

                string? delta;
                try
                {
                    using var document = JsonDocument.Parse(data);
                    delta = document.RootElement
                        .GetProperty("choices")[0]
                        .GetProperty("delta")
                        .TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String
                        ? content.GetString()
                        : null;
                }
                catch (JsonException error)
                {
                    throw new ProviderRequestException(ProviderFailureCategory.InvalidResponse, "模型服务返回了无法识别的串流数据。", error);
                }
                catch (KeyNotFoundException error)
                {
                    throw new ProviderRequestException(ProviderFailureCategory.InvalidResponse, "模型服务返回了无法识别的串流数据。", error);
                }

                if (!string.IsNullOrEmpty(delta))
                {
                    yield return new ChatStreamChunk(delta);
                }
            }

            if (!done)
            {
                yield return new ChatStreamChunk(string.Empty, IsComplete: true);
            }
        }
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

    private static void EnsureSuccess(HttpResponseMessage response)
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
        throw new ProviderRequestException(category, category switch
        {
            ProviderFailureCategory.Authentication => "模型服务认证失败。",
            ProviderFailureCategory.RateLimited => "模型服务请求过于频繁。",
            ProviderFailureCategory.ServiceUnavailable => "模型服务暂时不可用。",
            _ => "模型服务请求失败。",
        });
    }
}
