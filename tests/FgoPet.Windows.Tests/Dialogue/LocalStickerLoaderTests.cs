using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FgoPet.App.Dialogue;
using Xunit;

namespace FgoPet.Windows.Tests.Dialogue;

public sealed class LocalStickerLoaderTests
{
    [Fact]
    public Task Local_loader_accepts_pinned_png_and_rejects_unknown_paths_replacements_and_size_overflow() => StaRunner.RunAsync(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "fgopet-local-sticker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[16], 8)));
        using var output = new MemoryStream(); encoder.Save(output);
        var bytes = output.ToArray();
        var file = Path.Combine(root, "small.png");
        var asset = new StickerAsset("fixture.small", "happy", "synthetic", "small.png", Convert.ToHexString(SHA256.HashData(bytes)));
        try
        {
            await File.WriteAllBytesAsync(file, bytes);
            var loader = new LocalStickerLoader(root, [asset]);
            var data = await loader.LoadAsync(asset.Id, default);
            Assert.StartsWith("data:image/png;base64,", data);
            Assert.Null(await loader.LoadAsync("../small.png", default));
            Assert.Null(await loader.LoadAsync("file:///private.png", default));
            Assert.Null(await loader.LoadAsync("fixture.unknown", default));
            Assert.Null(await new LocalStickerLoader("relative", [asset]).LoadAsync(asset.Id, default));
            Assert.Null(await new LocalStickerLoader(root, [asset with { RelativePath = "../small.png" }]).LoadAsync(asset.Id, default));
            await File.WriteAllBytesAsync(file, [137, 80, 78, 71, 13, 10, 26, 10]);
            Assert.Null(await loader.LoadAsync(asset.Id, default));
            await File.WriteAllBytesAsync(file, new byte[4 * 1024 * 1024 + 1]);
            Assert.Null(await loader.LoadAsync(asset.Id, default));
            File.Delete(file);
            Assert.Null(await loader.LoadAsync(asset.Id, default));
        }
        finally { if (File.Exists(file)) File.Delete(file); Directory.Delete(root); }
    });
}
