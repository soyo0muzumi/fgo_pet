using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FgoPet.Core.Secrets;

namespace FgoPet.Plugin.Qq.Transport;

// Current documented host supersedes the older bots.qq.com / api.sgroup.qq.com SDK defaults.
// https://bot.q.qq.com/wiki/develop/api-v2/dev-prepare/access-token.html
// https://bot.q.qq.com/wiki/develop/api-v2/openapi/wss/url_get.html
// https://bot.q.qq.com/wiki/develop/api-v2/autogen/api/v2_users_user_openid_messages.post.html
internal sealed class QqBotApiClient(HttpClient http, ICredentialReader credentials, QqBotConnection connection,
    TimeProvider time)
{
    private static readonly Uri ApiBase = new("https://api.bot.qq.com/");
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private TokenSnapshot? _cachedToken;

    internal void InvalidateToken() => Volatile.Write(ref _cachedToken, null);

    internal async Task<string> GetAccessTokenAsync(CancellationToken token)
    {
        await _tokenGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var cached = Volatile.Read(ref _cachedToken);
            if (cached is not null && time.GetUtcNow() < cached.ExpiresAt - TimeSpan.FromSeconds(60)) return cached.Value;
            string? secret;
            try { secret = await credentials.ReadAsync(connection.AppSecretCredentialTarget, token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception) { throw new QqTransportException("QQ_CREDENTIAL_UNAVAILABLE", terminal: true); }
            if (string.IsNullOrWhiteSpace(secret) || secret.Length > 4096)
                throw new QqTransportException("QQ_CREDENTIAL_MISSING", terminal: true);

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10), time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
            using var request = Request(HttpMethod.Post, "app/getAppAccessToken");
            request.Content = JsonContent.Create(new { appId = connection.AppId, clientSecret = secret });
            var requestedAt = time.GetUtcNow();
            try
            {
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
                EnsureStatus(response, tokenRequest: true);
                var data = await ReadJsonAsync(response, 16 * 1024, linked.Token).ConfigureAwait(false);
                if (TryErrorCode(data, out var code))
                    throw new QqTransportException(code == 100001 ? "QQ_RATE_LIMITED" : "QQ_AUTHENTICATION_FAILED",
                        terminal: code is 100007 or 100016 or 10004, retryDelay: code == 100001 ? TimeSpan.FromMinutes(1) : null);
                if (!QqGatewayProtocol.TryString(data, "access_token", 8192, out var value)
                    || value.Any(character => character is <= ' ' or >= '\u007f')
                    || !data.TryGetProperty("expires_in", out var expiry) || !TrySeconds(expiry, out var seconds)
                    || seconds is <= 0 or > 86400) throw new QqTransportException("QQ_TOKEN_RESPONSE_INVALID");
                Volatile.Write(ref _cachedToken, new(value, requestedAt.AddSeconds(seconds)));
                return value;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new QqTransportException("QQ_TOKEN_TIMEOUT"); }
            catch (HttpRequestException) { throw new QqTransportException("QQ_TOKEN_NETWORK_FAILED"); }
            catch (IOException) { throw new QqTransportException("QQ_TOKEN_NETWORK_FAILED"); }
            catch (JsonException) { throw new QqTransportException("QQ_TOKEN_RESPONSE_INVALID"); }
        }
        finally { _tokenGate.Release(); }
    }

    internal async Task<Uri> GetGatewayAsync(string accessToken, CancellationToken token)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20), time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        using var request = Request(HttpMethod.Get, "gateway", accessToken);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            EnsureStatus(response, tokenRequest: false);
            var data = await ReadJsonAsync(response, 16 * 1024, linked.Token).ConfigureAwait(false);
            if (TryErrorCode(data, out var code)) throw new QqTransportException("QQ_GATEWAY_REJECTED",
                retryDelay: code == 100001 ? TimeSpan.FromMinutes(1) : null);
            if (!QqGatewayProtocol.TryString(data, "url", 2048, out var address)
                || !Uri.TryCreate(address, UriKind.Absolute, out var uri) || !QqGatewayProtocol.IsGatewayAllowed(uri))
                throw new QqTransportException("QQ_GATEWAY_ADDRESS_DENIED", terminal: true);
            return uri;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new QqTransportException("QQ_GATEWAY_TIMEOUT"); }
        catch (HttpRequestException) { throw new QqTransportException("QQ_GATEWAY_NETWORK_FAILED"); }
        catch (IOException) { throw new QqTransportException("QQ_GATEWAY_NETWORK_FAILED"); }
        catch (JsonException) { throw new QqTransportException("QQ_GATEWAY_RESPONSE_INVALID"); }
    }

    internal async Task<QqSendResult> ReplyAsync(string openId, string messageId, string text, int sequence,
        CancellationToken token)
    {
        string accessToken;
        try { accessToken = await GetAccessTokenAsync(token).ConfigureAwait(false); }
        catch (QqTransportException error) { return new(false, error.Code); }
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30), time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
            using var request = Request(HttpMethod.Post, $"v2/users/{Uri.EscapeDataString(openId)}/messages", accessToken);
            request.Content = JsonContent.Create(new { content = text, msg_type = 0, msg_id = messageId, msg_seq = sequence });
            try
            {
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
                // An explicit 401 proves rejection. Retry once with the SAME msg_id + msg_seq, never a new identity.
                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
                {
                    InvalidateToken();
                    try { accessToken = await GetAccessTokenAsync(token).ConfigureAwait(false); }
                    catch (QqTransportException error) { return new(false, error.Code); }
                    continue;
                }
                if ((int)response.StatusCode is >= 300 and < 400) return new(false, "QQ_REDIRECT_DENIED");
                if (!response.IsSuccessStatusCode) return new(false,
                    (int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout ? "QQ_SEND_UNKNOWN"
                    : response.StatusCode == HttpStatusCode.TooManyRequests ? "QQ_RATE_LIMITED" : "QQ_SEND_REJECTED");
                var data = await ReadJsonAsync(response, 64 * 1024, linked.Token).ConfigureAwait(false);
                if (TryErrorCode(data, out _)) return new(false, "QQ_SEND_REJECTED");
                return QqGatewayProtocol.TryString(data, "id", 512, out _)
                    ? new(true) : new(false, "QQ_SEND_UNKNOWN");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is OperationCanceledException or HttpRequestException or IOException
                or JsonException or QqTransportException)
            {
                // Once SendAsync was entered a lost response does not establish that QQ rejected the message.
                return new(false, "QQ_SEND_UNKNOWN");
            }
        }
        return new(false, "QQ_SEND_REJECTED");
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string? accessToken = null)
    {
        var request = new HttpRequestMessage(method, new Uri(ApiBase, path));
        request.Headers.UserAgent.ParseAdd("FgoPet-Qq/1.0");
        if (accessToken is not null) request.Headers.Authorization = new AuthenticationHeaderValue("QQBot", accessToken);
        return request;
    }

    private static void EnsureStatus(HttpResponseMessage response, bool tokenRequest)
    {
        var status = (int)response.StatusCode;
        if (status is >= 300 and < 400) throw new QqTransportException("QQ_REDIRECT_DENIED", terminal: true);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new QqTransportException("QQ_RATE_LIMITED", retryDelay: TimeSpan.FromMinutes(1));
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new QqTransportException("QQ_AUTHENTICATION_FAILED", terminal: tokenRequest, refreshToken: true);
        if (!response.IsSuccessStatusCode)
            throw new QqTransportException("QQ_API_UNAVAILABLE", terminal: status is >= 400 and < 500);
    }

    internal static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, int maximumBytes, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength > maximumBytes) throw new QqTransportException("QQ_RESPONSE_TOO_LARGE");
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, token).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > maximumBytes) throw new QqTransportException("QQ_RESPONSE_TOO_LARGE");
            buffer.Write(chunk, 0, read);
        }
        using var document = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 24 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
        return document.RootElement.Clone();
    }

    private static bool TryErrorCode(JsonElement data, out int code)
    {
        code = 0;
        if (!data.TryGetProperty("code", out var property)) return false;
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out code)) throw new JsonException();
        return code != 0;
    }

    private static bool TrySeconds(JsonElement data, out int seconds)
    {
        seconds = 0;
        return data.ValueKind == JsonValueKind.Number ? data.TryGetInt32(out seconds)
            : data.ValueKind == JsonValueKind.String && int.TryParse(data.GetString(),
                System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out seconds);
    }

    private sealed record TokenSnapshot(string Value, DateTimeOffset ExpiresAt);
}
