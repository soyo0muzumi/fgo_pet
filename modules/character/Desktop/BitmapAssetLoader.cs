using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FgoPet.Core.Geometry;
using FgoPet.Core.Packs;
using FgoPet.Infrastructure.Packs;

namespace FgoPet.App.Portraits;

/// <summary>
/// Decodes every validated asset with <see cref="BitmapCacheOption.OnLoad"/>, freezes it,
/// verifies visible Alpha and composition geometry, releases file handles, and builds an
/// immutable <see cref="PortraitSnapshot"/> with precomputed source Alpha masks.
/// </summary>
public static class BitmapAssetLoader
{
    public static PortraitSnapshot LoadValidated(ValidatedAppearance appearance)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        var manifest = appearance.Manifest;
        var root = appearance.Root;

        var images = new Dictionary<string, BitmapSource>(StringComparer.Ordinal);
        var masks = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var bodyWidth = 0;
        var bodyHeight = 0;

        foreach (var asset in manifest.Assets)
        {
            var fullPath = Path.Combine(root, asset.RelativePath);
            var image = Decode(fullPath, asset.RelativePath);

            var mask = BuildAlphaMask(image, asset.RelativePath);
            images.Add(asset.StableId, image);
            masks.Add(asset.StableId, mask);

            if (asset.AssetType == ArtAssetKind.Body)
            {
                bodyWidth = image.PixelWidth;
                bodyHeight = image.PixelHeight;
            }
            else if (image.PixelWidth != manifest.Composition.OverlaySize.Width
                     || image.PixelHeight != manifest.Composition.OverlaySize.Height)
            {
                throw Failed(PackErrorCode.CompositionOutOfBounds,
                    $"表情素材 {asset.StableId} 尺寸为 {image.PixelWidth}x{image.PixelHeight}，与 overlay_size 不一致。",
                    asset.RelativePath);
            }
        }

        ValidateGeometry(manifest, bodyWidth, bodyHeight);

        return new PortraitSnapshot(
            images,
            manifest.Composition.BodyId,
            manifest.Composition.DefaultExpressionId,
            masks,
            PortraitSourceGeometry.FromManifest(manifest, bodyWidth, bodyHeight));
    }

    private static BitmapSource Decode(string path, string relativePath)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            frame.Freeze();
            return frame;
        }
        catch (Exception error) when (error is not PackFailureException)
        {
            throw Failed(PackErrorCode.ImageDecodeFailed, $"无法解码素材: {error.Message}", relativePath);
        }
    }

    private static byte[] BuildAlphaMask(BitmapSource image, string relativePath)
    {
        var bgra = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var width = bgra.PixelWidth;
        var height = bgra.PixelHeight;
        var pixels = new byte[width * height * 4];
        bgra.CopyPixels(pixels, width * 4, 0);

        var mask = new byte[width * height];
        var visible = false;
        for (var index = 0; index < mask.Length; index++)
        {
            var alpha = pixels[(index * 4) + 3];
            mask[index] = alpha;
            visible |= alpha != 0;
        }

        if (!visible)
        {
            throw Failed(PackErrorCode.ImageHasNoVisibleAlpha, "素材 Alpha 通道没有可见像素。", relativePath);
        }

        return mask;
    }

    private static void ValidateGeometry(AppearanceManifestV3 manifest, int bodyWidth, int bodyHeight)
    {
        var composition = manifest.Composition;
        var offset = composition.OverlayOffset;
        var overlay = composition.OverlaySize;
        var panel = composition.PanelAnchor;

        if (offset.X < 0 || offset.Y < 0
            || offset.X + overlay.Width > bodyWidth
            || offset.Y + overlay.Height > bodyHeight)
        {
            throw Failed(PackErrorCode.CompositionOutOfBounds, "表情覆盖层越出身体画布。");
        }
        if (panel.X < 0 || panel.Y < 0 || panel.X > bodyWidth || panel.Y > bodyHeight)
        {
            throw Failed(PackErrorCode.CompositionOutOfBounds, "面板锚点越出身体画布。");
        }
    }

    private static PackFailureException Failed(PackErrorCode code, string message, string? relativePath = null)
        => new(new PackFailure(code, message, relativePath));
}