using System.IO;
using FgoPet.Core.Dialogue;

namespace FgoPet.App.Dialogue;

/// <summary>One bounded transfer from the trusted chat surface; never a path or a send authorization.</summary>
internal sealed class ChatImageTransfer(TimeProvider? time = null) : IDisposable
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);
    private const int MaxRecentTransfers = 128;
    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private readonly Dictionary<string, DateTimeOffset> recentIds = new(StringComparer.Ordinal);
    private MemoryStream? stream;
    private string? transferId, mimeType;
    private DateTimeOffset started;
    private bool disposed;
    public bool Active { get { if (stream is not null && clock.GetUtcNow() - started >= Lifetime) Clear(); return stream is not null; } }

    public byte[]? Append(string id, string mime, long offset, string data, bool complete)
    {
        if (disposed) throw new InvalidOperationException("CHAT_IMAGE_TRANSFER_INVALID");
        var now = clock.GetUtcNow();
        if (stream is not null && now - started >= Lifetime) Clear();
        foreach (var expired in recentIds.Where(pair => pair.Key != transferId && pair.Value <= now).Select(pair => pair.Key).ToArray())
            recentIds.Remove(expired);
        // A late tail belongs to the superseded operation, not the current buffer.
        // Keep one-shot IDs for the transfer lifetime; reject excess starts rather
        // than evicting a still-protected ID and allowing its replay.
        if ((offset > 0 && stream is not null && transferId != id)
            || (offset == 0 && (recentIds.ContainsKey(id) || recentIds.Count >= MaxRecentTransfers)))
            throw new InvalidOperationException("CHAT_IMAGE_TRANSFER_INVALID");
        try
        {
            if (!Guid.TryParseExact(id, "N", out _) || mime is not ("image/png" or "image/jpeg")
                || data.Length is < 1 or > 49152 || offset < 0) throw new InvalidOperationException();
            if (offset == 0)
            {
                Clear(); recentIds.Add(id, now + Lifetime);
                stream = new(); transferId = id; mimeType = mime; started = now;
            }
            if (stream is null || transferId != id || mimeType != mime || offset != stream.Length
                || now - started >= Lifetime) throw new InvalidOperationException();
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
    public void Clear()
    {
        if (transferId is not null) recentIds[transferId] = clock.GetUtcNow() + Lifetime;
        stream?.Dispose(); stream = null; transferId = mimeType = null;
    }
    public void Dispose() { disposed = true; Clear(); recentIds.Clear(); }
}
