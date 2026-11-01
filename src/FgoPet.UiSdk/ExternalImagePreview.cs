using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FgoPet.UiSdk;

/// <summary>Opt-in presentation transport. No credentials, redirects, local networks or disk cache.</summary>
public sealed class ExternalImagePreview : IDisposable
{
    private const int MaxBytes = 4 * 1024 * 1024;
    private readonly HttpClient _client;

    public ExternalImagePreview() : this(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
        ConnectCallback = ConnectPublicAsync,
    }) { }

    internal ExternalImagePreview(HttpMessageHandler handler) => _client = new(handler) { Timeout = TimeSpan.FromSeconds(12) };

    public async Task<string?> LoadAsync(Uri url, CancellationToken token)
    {
        if (!IsAllowedUrl(url)) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        var requestToken = timeout.Token;
        try
        {
            using var response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, requestToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength > MaxBytes) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(requestToken).ConfigureAwait(false);
            using var bytes = new MemoryStream();
            var buffer = new byte[16 * 1024];
            int count;
            while ((count = await stream.ReadAsync(buffer, requestToken).ConfigureAwait(false)) > 0)
            {
                if (bytes.Length + count > MaxBytes) return null;
                bytes.Write(buffer, 0, count);
            }
            requestToken.ThrowIfCancellationRequested();
            return Sanitize(bytes.ToArray());
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
        catch (Exception error) when (error is HttpRequestException or IOException or NotSupportedException or ArgumentException or OverflowException)
        { return null; }
    }

    internal static bool IsAllowedUrl(Uri url) => url.IsAbsoluteUri && url.Scheme == "https" && url.IsDefaultPort
        && url.AbsoluteUri.Length <= 2048 && url.UserInfo.Length == 0 && !url.IsLoopback
        && url.Host.Contains('.') && !url.IdnHost.EndsWith(".invalid", StringComparison.OrdinalIgnoreCase)
        && (!IPAddress.TryParse(url.DnsSafeHost.Trim('[', ']'), out var ip) || IsPublicAddress(ip));

    internal static bool IsPublicAddress(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        var b = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return (b[0] & 0xe0) == 0x20 && b[1] != 2
                && !(b[0] == 0x20 && b[1] == 1 && (b[2] == 0 && b[3] == 0 || b[2] == 0x0d && b[3] == 0xb8));
        return b[0] is > 0 and < 224 && b[0] is not (10 or 127)
            && !(b[0] == 100 && b[1] is >= 64 and <= 127)
            && !(b[0] == 169 && b[1] == 254) && !(b[0] == 172 && b[1] is >= 16 and <= 31)
            && !(b[0] == 192 && (b[1] == 168 || b[1] == 0 && b[2] is 0 or 2))
            && !(b[0] == 198 && (b[1] is 18 or 19 || b[1] == 51 && b[2] == 100))
            && !(b[0] == 203 && b[1] == 0 && b[2] == 113);
    }

    private static async ValueTask<Stream> ConnectPublicAsync(SocketsHttpConnectionContext context, CancellationToken token)
    {
        // Connect to the validated addresses themselves, so a second DNS lookup cannot rebind to a local service.
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token).ConfigureAwait(false);
        if (addresses.Length == 0 || addresses.Any(ip => !IsPublicAddress(ip))) throw new HttpRequestException("IMAGE_HOST_REJECTED");
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), token).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException) { socket.Dispose(); }
            catch { socket.Dispose(); throw; }
        }
        throw new HttpRequestException("IMAGE_CONNECT_FAILED");
    }

    private static string? Sanitize(byte[] bytes)
    {
        // Raster signatures only; never hand SVG, HTML, animation or remote URLs to WebView.
        if (!(bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            && !(bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff)) return null;
        using var input = new MemoryStream(bytes, writable: false);
        var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        if (decoder.Frames.Count != 1) return null;
        var frame = decoder.Frames[0];
        var width = frame.PixelWidth; var height = frame.PixelHeight;
        if (width is < 1 or > 4096 || height is < 1 or > 4096 || (long)width * height > 4 * 1024 * 1024) return null;
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[checked(width * height * 4)];
        converted.CopyPixels(pixels, width * 4, 0);
        var clean = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(clean));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.Length <= MaxBytes ? "data:image/png;base64," + Convert.ToBase64String(output.ToArray()) : null;
    }

    public void Dispose() => _client.Dispose();
}
