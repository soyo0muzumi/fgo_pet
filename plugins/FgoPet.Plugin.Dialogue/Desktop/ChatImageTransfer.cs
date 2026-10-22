using System.IO;
using FgoPet.Core.Dialogue;

namespace FgoPet.App.Dialogue;

/// <summary>One bounded transfer from the trusted chat surface; never a path or a send authorization.</summary>
internal sealed class ChatImageTransfer : IDisposable
{
    private MemoryStream? stream;
    private string? transferId, mimeType;
    private DateTimeOffset started;
    public bool Active { get { if (DateTimeOffset.UtcNow - started > TimeSpan.FromSeconds(60)) Clear(); return stream is not null; } }

    public byte[]? Append(string id, string mime, long offset, string data, bool complete)
    {
        try
        {
            if (!Guid.TryParseExact(id, "N", out _) || mime is not ("image/png" or "image/jpeg")
                || data.Length is < 1 or > 49152 || offset < 0) throw new InvalidOperationException();
            if (offset == 0)
            {
                Clear(); stream = new(); transferId = id; mimeType = mime; started = DateTimeOffset.UtcNow;
            }
            if (stream is null || transferId != id || mimeType != mime || offset != stream.Length
                || DateTimeOffset.UtcNow - started > TimeSpan.FromSeconds(60)) throw new InvalidOperationException();
            var bytes = Convert.FromBase64String(data);
            if (bytes.Length == 0 || stream.Length + bytes.Length > SelectedImageFormat.MaxSourceBytes)
                throw new InvalidOperationException();
            stream.Write(bytes);
            if (!complete) return null;
            var result = stream.ToArray(); Clear(); return result;
        }
        catch (Exception error) when (error is FormatException or InvalidOperationException)
        { Clear(); throw new InvalidOperationException("CHAT_IMAGE_TRANSFER_INVALID"); }
    }
    public void Clear() { stream?.Dispose(); stream = null; transferId = mimeType = null; }
    public void Dispose() => Clear();
}
