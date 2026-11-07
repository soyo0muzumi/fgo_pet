using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FgoPet.App.Dialogue;
using FgoPet.Extensibility;
using Xunit;

namespace FgoPet.Windows.Tests.Dialogue;

public sealed class ManagedStickerLibraryTests
{
    [Fact]
    public Task Import_is_persistent_sanitized_deduplicated_and_replay_safe() => StaRunner.RunAsync(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "fgopet-web-stickers-" + Guid.NewGuid().ToString("N"));
        var calls = 0;
        var image = Png(1);
        var library = new ManagedStickerLibrary(root, (_, _) => { calls++; return Task.FromResult<string?>(image); });
        var source = new Uri("https://images.example/reaction.png?tracking=value");
        var execution = new ToolExecutionContext("run", 1, "call", "key");
        try
        {
            Assert.False(Directory.Exists(root));
            Assert.Equal(30, library.ReadAssets().Count);
            Assert.Empty(new ManagedStickerLibrary(root, originalRoot: Path.Combine(root, "not-installed")).ReadAssets());
            Assert.False(Directory.Exists(root));
            var added = await library.ImportAsync(source, "大笑", "laugh", execution, () => true, default);
            Assert.True(StickerPolicy.IsWebAssetId(added.Id));
            Assert.Equal("https://images.example/reaction.png", added.Source);
            Assert.NotNull(await library.LoadAsync(added.Id, default));
            Assert.Equal(added, await library.ImportAsync(source, "大笑", "laugh", execution, () => true, default));
            Assert.Equal(1, calls);
            Assert.Equal("STICKER_REPLAY_CONFLICT", (await Assert.ThrowsAsync<StickerLibraryException>(() =>
                library.ImportAsync(source, "不同", "laugh", execution, () => true, default))).Code);
            Assert.Equal("STICKER_RUN_LIMIT", (await Assert.ThrowsAsync<StickerLibraryException>(() =>
                library.ImportAsync(source, "大笑", "laugh", execution with { IdempotencyKey = "second" }, () => true, default))).Code);
            var duplicate = await library.ImportAsync(source, "笑", "playful", execution with { RunId = "run-two", IdempotencyKey = "other" }, () => true, default);
            Assert.Equal(added.Id, duplicate.Id);
            Assert.Single(new ManagedStickerLibrary(root).ReadCollected());
            var candidate = new StickerCandidate(added.Id, added.Intent, "enhance", "high", "playful", "after");
            Assert.Contains(Enumerable.Range(0, 200), i => StickerPolicy.Select(candidate, [], i.ToString(), "好笑。", catalog: library.ReadAssets())?.AssetId == added.Id);
            Assert.Null(StickerPolicy.Select(candidate, [], "key", "好笑。"));
            Assert.DoesNotContain("大笑", StickerPolicy.BuildPrompt(library.ReadAssets()));
            var full = Enumerable.Range(0, ManagedStickerLibrary.MaxEntries).Select(i => added with {
                Id = "web." + i.ToString("x48"), Sha256 = i.ToString("X48") + new string('A', 16), KeyHash = i.ToString("X64"), RunHash = i.ToString("X64") }).ToArray();
            var catalogPath = Path.Combine(root, "catalog.json");
            await File.WriteAllTextAsync(catalogPath, JsonSerializer.Serialize(new { Version = 1, Entries = full }));
            Assert.Equal(100, library.ReadCollected().Count);
            Assert.True(StickerPolicy.BuildPrompt(library.ReadAssets()).Length < 12_000);
            var beforeQuota = calls;
            Assert.Equal("STICKER_LIBRARY_FULL", (await Assert.ThrowsAsync<StickerLibraryException>(() =>
                library.ImportAsync(source, "笑", "laugh", execution with { RunId = "full", IdempotencyKey = "full" }, () => true, default))).Code);
            Assert.Equal(beforeQuota, calls);
            await File.WriteAllTextAsync(catalogPath, "{\"Version\":1,\"Entries\":[{\"Id\":\"../../private\"}]}");
            Assert.Empty(library.ReadCollected());
            Assert.Null(await library.LoadAsync(added.Id, default));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    });

    [Fact]
    public Task Revocation_cancellation_invalid_content_and_nonpublic_urls_never_publish_an_entry() => StaRunner.RunAsync(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "fgopet-web-stickers-denial-" + Guid.NewGuid().ToString("N"));
        var authorized = true;
        var library = new ManagedStickerLibrary(root, (_, _) => { authorized = false; return Task.FromResult<string?>(Png(1)); });
        var execution = new ToolExecutionContext("run", 1, "call", "key");
        var source = new Uri("https://images.example/image.png");
        try
        {
            Assert.Equal("STICKER_AUTHORIZATION_CHANGED", (await Assert.ThrowsAsync<StickerLibraryException>(() =>
                library.ImportAsync(source, "笑", "laugh", execution, () => authorized, default))).Code);
            Assert.Empty(library.ReadCollected());
            Assert.False(Directory.Exists(root));
            Assert.Equal("STICKER_INVALID_ARGUMENTS", (await Assert.ThrowsAsync<StickerLibraryException>(() =>
                library.ImportAsync(new("https://127.0.0.1/private"), "笑", "laugh", execution, () => true, default))).Code);
            Assert.Equal("STICKER_INVALID_ARGUMENTS", (await Assert.ThrowsAsync<StickerLibraryException>(() =>
                library.ImportAsync(source, "笑", "care", execution, () => true, default))).Code);
            var bad = new ManagedStickerLibrary(root, (_, _) => Task.FromResult<string?>("data:image/png;base64,PHN2Zz4="));
            Assert.Equal("STICKER_IMAGE_INVALID", (await Assert.ThrowsAsync<StickerLibraryException>(() =>
                bad.ImportAsync(source, "笑", "laugh", execution, () => true, default))).Code);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => bad.ImportAsync(source, "笑", "laugh", execution, () => true, cancelled.Token));
            Assert.Empty(bad.ReadCollected());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    });

    internal static string Png(byte value)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null,
            new byte[] { value, 10, 20, 255, 10, value, 20, 255, 10, 20, value, 255, value, 20, 10, 255 }, 8)));
        using var output = new MemoryStream(); encoder.Save(output);
        return "data:image/png;base64," + Convert.ToBase64String(output.ToArray());
    }
}
