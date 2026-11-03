using System.Net;
using System.Net.Sockets;

namespace FgoPet.Plugin.Qq.Transport;

/// <summary>Official QQ attachment origins only. No redirects, cookies, credentials or body logging.</summary>
public sealed class QqImageDownloader : IDisposable
{
    internal const int MaxBytes = 8 * 1024 * 1024;
    private readonly HttpClient http;
    public QqImageDownloader() : this(new SocketsHttpHandler {
        AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(10),
        ConnectCallback = ConnectAsync
    }) { }
    internal QqImageDownloader(HttpMessageHandler handler) => http = new(handler) { Timeout = TimeSpan.FromSeconds(20) };

    public static bool IsAllowedUrl(string url) => url.Length <= 4096 && !url.Any(char.IsControl) && Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.Port == 443 && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0
        && uri.IdnHost is "multimedia.nt.qq.com.cn" or "gchat.qpic.cn";

    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken token)
    {
        // Pin the resolved address to this socket; re-resolution cannot redirect the request into the local network.
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token);
        foreach (var address in addresses)
        {
            var ip = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
            var b = ip.GetAddressBytes();
            if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) ||
                ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast ||
                (ip.AddressFamily == AddressFamily.InterNetwork && (b[0] is 0 or 10 or 127 || b[0] >= 224 ||
                    b[0] == 169 && b[1] == 254 || b[0] == 172 && b[1] is >= 16 and <= 31 ||
                    b[0] == 192 && b[1] == 168 || b[0] == 100 && b[1] is >= 64 and <= 127)) ||
                (ip.AddressFamily == AddressFamily.InterNetworkV6 && (b[0] & 0xfe) == 0xfc)) continue;
            var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try { await socket.ConnectAsync(new IPEndPoint(ip, context.DnsEndPoint.Port), token); return new NetworkStream(socket, ownsSocket: true); }
            catch (SocketException) { socket.Dispose(); }
            catch { socket.Dispose(); throw; }
        }
        throw new HttpRequestException("QQ_IMAGE_ADDRESS_DENIED");
    }

    public async Task<QqImageInput?> DownloadAsync(QqImageAttachment image, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!IsAllowedUrl(image.Url) || image.ContentType is not ("image/png" or "image/jpeg")) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var downloadToken = timeout.Token;
        try
        {
            using var response = await http.GetAsync(image.Url, HttpCompletionOption.ResponseHeadersRead, downloadToken);
            downloadToken.ThrowIfCancellationRequested();
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is > MaxBytes ||
                response.Content.Headers.ContentType?.MediaType != image.ContentType) return null;
            await using var input = await response.Content.ReadAsStreamAsync(downloadToken);
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await input.ReadAsync(buffer, downloadToken)) != 0)
            {
                if (output.Length + read > MaxBytes) return null;
                output.Write(buffer, 0, read);
            }
            downloadToken.ThrowIfCancellationRequested();
            return output.Length == 0 ? null : new(output.ToArray(), image.ContentType);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
        catch (HttpRequestException) { return null; }
        catch (IOException) { return null; }
    }
    public void Dispose() => http.Dispose();
}
