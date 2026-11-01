using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace FgoPet.Plugin.SocialSources;

/// <summary>
/// One bounded HTTP boundary shared by every platform reader. Redirects, cookies and automatic
/// decompression stay off so a platform cannot redirect a read to another host or replay a session.
/// </summary>
internal sealed class SocialHttpGateway : IDisposable
{
    internal const int MaxResponseBytes = 512 * 1024;
    private static readonly TimeSpan TotalBudget = TimeSpan.FromSeconds(20);

    private readonly HttpClient _http;

    public SocialHttpGateway()
        : this(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        }) { }

    internal SocialHttpGateway(HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    internal async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpRequestMessage request,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TotalBudget);
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                deadline.Token).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength is > MaxResponseBytes)
                throw new SocialSourceException("SOCIAL_RESPONSE_TOO_LARGE");
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            var buffer = new byte[16 * 1024];
            using var collected = new MemoryStream();
            int read;
            while ((read = await stream.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) > 0)
            {
                if (collected.Length + read > MaxResponseBytes)
                    throw new SocialSourceException("SOCIAL_RESPONSE_TOO_LARGE");
                collected.Write(buffer, 0, read);
            }
            return (response.StatusCode, Decode(collected.ToArray(), response.Content.Headers.ContentEncoding));
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new SocialSourceException("SOCIAL_TIMEOUT"); }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException) { throw new SocialSourceException("SOCIAL_NETWORK_FAILED"); }
    }

    private static string Decode(byte[] bytes, ICollection<string> encodings)
    {
        // Decode explicitly: the handler never negotiates compression, but a server may still answer gzip.
        if (encodings.Any(value => value.Equals("gzip", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                using var input = new MemoryStream(bytes);
                using var gzip = new System.IO.Compression.GZipStream(input,
                    System.IO.Compression.CompressionMode.Decompress);
                using var output = new MemoryStream();
                gzip.CopyTo(output);
                return Encoding.UTF8.GetString(output.ToArray());
            }
            catch (Exception error) when (error is InvalidDataException or IOException)
            { throw new SocialSourceException("SOCIAL_RESPONSE_INVALID"); }
        }
        return Encoding.UTF8.GetString(bytes);
    }

    internal static void AddCommonHeaders(HttpRequestMessage request, string? cookie)
    {
        request.Headers.TryAddWithoutValidation("User-Agent", SocialHttpUserAgent);
        request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrEmpty(cookie)) request.Headers.TryAddWithoutValidation("Cookie", cookie);
    }

    internal const string SocialHttpUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/131.0.0.0 Safari/537.36";

    public void Dispose() => _http.Dispose();
}
