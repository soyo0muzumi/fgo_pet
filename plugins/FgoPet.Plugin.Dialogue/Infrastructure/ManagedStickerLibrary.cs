using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FgoPet.Extensibility;
using FgoPet.UiSdk;

namespace FgoPet.App.Dialogue;

public sealed class StickerLibraryException(string code) : Exception(code) { public string Code { get; } = code; }
public sealed record CollectedSticker(string Id, string Intent, string Label, string Sha256, string Source,
    DateTimeOffset AddedAtUtc, int ByteLength, string RunHash, string KeyHash, string RequestHash)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public StickerAsset Asset => new(Id, Intent, Label, "images/" + Sha256 + ".png", Sha256, External: true);
}

/// <summary>Bounded append-only collection. Labels and sources are untrusted data, never host instructions.</summary>
public sealed class ManagedStickerLibrary : IStickerCatalogSource
{
    public const int MaxEntries = 100;
    public const long MaxTotalBytes = 64 * 1024 * 1024;
    private readonly string root;
    private readonly Func<Uri, CancellationToken, Task<string?>>? download;
    private readonly string? originalRoot;
    private readonly SemaphoreSlim gate = new(1, 1);
    private sealed record Index(int Version, List<CollectedSticker> Entries);
    private static readonly JsonSerializerOptions Json = new() { MaxDepth = 8,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
    public ManagedStickerLibrary(string root, string? originalRoot = null) : this(root, null, originalRoot) { }
    internal ManagedStickerLibrary(string root, Func<Uri, CancellationToken, Task<string?>>? download, string? originalRoot = null)
    {
        if (!Path.IsPathFullyQualified(root) || root.StartsWith("\\\\", StringComparison.Ordinal)) throw new StickerLibraryException("STICKER_PATH_DENIED");
        this.root = Path.GetFullPath(root); this.download = download; this.originalRoot = originalRoot;
    }

    public IReadOnlyList<StickerAsset> ReadAssets() => StickerCatalog.Assets.Where(a => originalRoot is null
        || Path.IsPathFullyQualified(originalRoot) && File.Exists(Path.Combine(originalRoot, a.RelativePath)))
        .Concat(ReadCollected().Select(e => e.Asset)).ToArray();
    public IReadOnlyList<CollectedSticker> ReadCollected()
    {
        try { return ReadIndex().Entries.AsReadOnly(); }
        catch (Exception error) when (error is StickerLibraryException or IOException or UnauthorizedAccessException or JsonException) { return []; }
    }

    public async Task<CollectedSticker> ImportAsync(Uri url, string label, string intent, ToolExecutionContext execution,
        Func<bool> authorized, CancellationToken token)
    {
        if (!ExternalImagePreview.IsAllowedUrl(url) || !ValidLabel(label) || !StickerPolicy.IsWebIntent(intent)
            || execution.RunId is not { Length: > 0 and <= 128 } || execution.IdempotencyKey is not { Length: > 0 and <= 256 })
            throw new StickerLibraryException("STICKER_INVALID_ARGUMENTS");
        var runHash = Hash(execution.RunId); var keyHash = Hash(execution.IdempotencyKey);
        var requestHash = Hash(url.AbsoluteUri + "\0" + label + "\0" + intent);
        await gate.WaitAsync(token).ConfigureAwait(false);
        string? temporaryImage = null; string? temporaryIndex = null;
        try
        {
            Check();
            var initial = ReadIndex();
            if (Replay(initial) is { } replay) return replay;
            Capacity(initial);
            string? data;
            if (download is not null) data = await download(url, token).ConfigureAwait(false);
            else { using var transport = new ExternalImagePreview(); data = await transport.LoadAsync(url, token).ConfigureAwait(false); }
            Check();
            if (data is not { Length: <= 5_592_432 } || !data.StartsWith("data:image/png;base64,", StringComparison.Ordinal))
                throw new StickerLibraryException("STICKER_IMAGE_UNAVAILABLE");
            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(data[22..]);
                data = SelectedImageDecoder.Decode(bytes, ".png", token).ToDataUrl();
                bytes = Convert.FromBase64String(data[22..]);
            }
            catch (Exception error) when (error is FormatException or FgoPet.Core.Dialogue.SelectedImageException)
            { throw new StickerLibraryException("STICKER_IMAGE_INVALID"); }
            var digest = Convert.ToHexString(SHA256.HashData(bytes));
            SafePath("catalog.json"); Directory.CreateDirectory(root); Directory.CreateDirectory(SafePath("images"));
            using var lease = await AcquireFileLease(token).ConfigureAwait(false);
            var index = ReadIndex(); Check();
            if (Replay(index) is { } repeated) return repeated;
            if (index.Entries.FirstOrDefault(e => e.Sha256 == digest) is { } duplicate) return duplicate;
            Capacity(index);
            if (index.Entries.Sum(e => (long)e.ByteLength) + bytes.Length > MaxTotalBytes) throw new StickerLibraryException("STICKER_LIBRARY_FULL");
            var source = new UriBuilder(url) { Query = "", Fragment = "" }.Uri.AbsoluteUri;
            var entry = new CollectedSticker("web." + digest[..48].ToLowerInvariant(), intent, label, digest, source,
                DateTimeOffset.UtcNow, bytes.Length, runHash, keyHash, requestHash);
            if (index.Entries.Any(e => e.Id == entry.Id)) throw new StickerLibraryException("STICKER_HASH_CONFLICT");
            var imagePath = SafePath(entry.Asset.RelativePath);
            var physicalImages = new DirectoryInfo(SafePath("images")).EnumerateFiles().ToArray();
            if ((!File.Exists(imagePath) && physicalImages.Length >= MaxEntries)
                || physicalImages.Sum(f => f.Length) + (File.Exists(imagePath) ? 0 : bytes.Length) > MaxTotalBytes)
                throw new StickerLibraryException("STICKER_LIBRARY_FULL");
            if (File.Exists(imagePath))
            {
                if (new FileInfo(imagePath).Length != bytes.Length || HashFile(imagePath) != digest) throw new StickerLibraryException("STICKER_IMAGE_INVALID");
            }
            else
            {
                temporaryImage = SafePath("images/.new-" + Guid.NewGuid().ToString("N"));
                await WriteNew(temporaryImage, bytes, token).ConfigureAwait(false);
                Check(); File.Move(temporaryImage, imagePath); temporaryImage = null;
            }
            index.Entries.Add(entry);
            var encoded = JsonSerializer.SerializeToUtf8Bytes(index, Json);
            if (encoded.Length > 128 * 1024) throw new StickerLibraryException("STICKER_LIBRARY_FULL");
            temporaryIndex = SafePath(".catalog-" + Guid.NewGuid().ToString("N"));
            await WriteNew(temporaryIndex, encoded, token).ConfigureAwait(false);
            Check(); File.Move(temporaryIndex, SafePath("catalog.json"), overwrite: true); temporaryIndex = null;
            return entry;
        }
        catch (OperationCanceledException) { throw; }
        catch (StickerLibraryException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        { throw new StickerLibraryException("STICKER_LIBRARY_WRITE_FAILED"); }
        finally { Cleanup(temporaryImage); Cleanup(temporaryIndex); gate.Release(); }

        void Check() { token.ThrowIfCancellationRequested(); if (!authorized()) throw new StickerLibraryException("STICKER_AUTHORIZATION_CHANGED"); }
        CollectedSticker? Replay(Index index)
        {
            var found = index.Entries.FirstOrDefault(e => e.KeyHash == keyHash);
            if (found is not null && found.RequestHash != requestHash) throw new StickerLibraryException("STICKER_REPLAY_CONFLICT");
            return found;
        }
        void Capacity(Index index)
        {
            if (index.Entries.Count >= MaxEntries || index.Entries.Sum(e => (long)e.ByteLength) >= MaxTotalBytes) throw new StickerLibraryException("STICKER_LIBRARY_FULL");
            if (index.Entries.Any(e => e.RunHash == runHash)) throw new StickerLibraryException("STICKER_RUN_LIMIT");
        }
    }

    public Task<string?> LoadAsync(string id, CancellationToken token)
    {
        var entry = ReadCollected().FirstOrDefault(e => e.Id == id);
        return entry is null ? Task.FromResult<string?>(null) : new LocalStickerLoader(root, [entry.Asset]).LoadAsync(id, token);
    }

    private Index ReadIndex()
    {
        var path = SafePath("catalog.json"); if (!File.Exists(path)) return new(1, []);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (file.Length is < 1 or > 128 * 1024) throw new StickerLibraryException("STICKER_LIBRARY_INVALID");
        using var document = JsonDocument.Parse(file, new JsonDocumentOptions { MaxDepth = 8 });
        Unique(document.RootElement);
        var index = document.RootElement.Deserialize<Index>(Json);
        if (index is not { Version: 1, Entries: not null } || index.Entries.Count > MaxEntries
            || index.Entries.Any(e => e is null || !StickerPolicy.IsWebAssetId(e.Id) || !StickerPolicy.IsWebIntent(e.Intent)
                || !ValidLabel(e.Label) || !ValidHash(e.Sha256) || !ValidHash(e.RunHash) || !ValidHash(e.KeyHash) || !ValidHash(e.RequestHash)
                || e.Id != "web." + e.Sha256[..48].ToLowerInvariant() || e.ByteLength is < 8 or > 4 * 1024 * 1024
                || e.AddedAtUtc.Offset != TimeSpan.Zero || !Uri.TryCreate(e.Source, UriKind.Absolute, out var source) || !ExternalImagePreview.IsAllowedUrl(source))
            || index.Entries.Select(e => e.Id).Distinct(StringComparer.Ordinal).Count() != index.Entries.Count
            || index.Entries.Sum(e => (long)e.ByteLength) > MaxTotalBytes) throw new StickerLibraryException("STICKER_LIBRARY_INVALID");
        return index;
    }

    private string SafePath(string relative)
    {
        if (!Path.IsPathFullyQualified(root) || root.StartsWith("\\\\", StringComparison.Ordinal)) throw new StickerLibraryException("STICKER_PATH_DENIED");
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new StickerLibraryException("STICKER_PATH_DENIED");
        for (FileSystemInfo? part = new FileInfo(path); part is not null; part = part is FileInfo f ? f.Directory : ((DirectoryInfo)part).Parent)
            if ((File.Exists(part.FullName) || Directory.Exists(part.FullName)) && (part.Attributes & FileAttributes.ReparsePoint) != 0) throw new StickerLibraryException("STICKER_PATH_DENIED");
        return path;
    }
    private async Task<FileStream> AcquireFileLease(CancellationToken token)
    {
        var path = SafePath(".collection-lock");
        for (var attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 20) { await Task.Delay(50, token).ConfigureAwait(false); }
        }
    }
    private static bool ValidLabel(string? label) => label is { Length: > 0 and <= 64 } && !string.IsNullOrWhiteSpace(label) && !label.Any(char.IsControl);
    private static void Unique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            { if (!names.Add(property.Name)) throw new StickerLibraryException("STICKER_LIBRARY_INVALID"); Unique(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Unique(item);
    }
    private static bool ValidHash(string? hash) => hash is { Length: 64 } && hash.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string HashFile(string path) { using var input = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(input)); }
    private static async Task WriteNew(string path, byte[] bytes, CancellationToken token)
    {
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await output.WriteAsync(bytes, token).ConfigureAwait(false); await output.FlushAsync(token).ConfigureAwait(false);
    }
    private static void Cleanup(string? path)
    { if (path is null) return; try { File.Delete(path); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { } }
}
