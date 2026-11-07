using System.IO;
using System.Security.Cryptography;
using FgoPet.Core.Dialogue;

namespace FgoPet.App.Dialogue;

/// <summary>Reads only pinned local artwork; never resolves a path supplied by the model or WebView.</summary>
internal sealed class LocalStickerLoader(string root, IReadOnlyList<StickerAsset>? assets = null)
{
    internal async Task<string?> LoadAsync(string assetId, CancellationToken token)
    {
        var asset = (assets ?? StickerCatalog.Assets).FirstOrDefault(a => a.Id == assetId);
        if (asset is null || !Path.IsPathFullyQualified(root) || root.StartsWith("\\\\", StringComparison.Ordinal)) return null;
        try
        {
            var basePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var path = Path.GetFullPath(Path.Combine(basePath, asset.RelativePath));
            if (!path.StartsWith(basePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || Path.GetExtension(path) != ".png") return null;
            // Reject links at every component. Pinned content hashes also prevent replacement races from exposing other bytes.
            for (FileSystemInfo? part = new FileInfo(path); part is not null;
                 part = part is FileInfo file ? file.Directory : ((DirectoryInfo)part).Parent)
                if (!part.Exists || (part.Attributes & FileAttributes.ReparsePoint) != 0) return null;
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, true);
            if (input.Length is <= 0 or > 4 * 1024 * 1024) return null;
            var bytes = new byte[(int)input.Length];
            await input.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
            if (input.ReadByte() != -1 || Convert.ToHexString(SHA256.HashData(bytes)) != asset.Sha256) return null;
            var clean = SelectedImageDecoder.Decode(bytes, ".png", token);
            return clean.ToDataUrl();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or SelectedImageException)
        { return null; }
    }
}
