using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FgoPet.RenderingProbe.Art;

public static class ArtBundleLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
    };

    public static ArtBundle Load(string manifestPath)
    {
        if (!Path.IsPathFullyQualified(manifestPath))
        {
            throw new InvalidDataException("Manifest path must be absolute.");
        }

        ManifestDto manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ManifestDto>(File.ReadAllText(manifestPath), JsonOptions)
                ?? throw new InvalidDataException("Manifest is empty.");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Manifest JSON is invalid.", error);
        }

        if (manifest.SchemaVersion != 2)
        {
            throw new InvalidDataException($"Unsupported schema version {manifest.SchemaVersion}; expected 2.");
        }
        if (manifest.Composition is null)
        {
            throw new InvalidDataException("Manifest composition is required.");
        }

        var root = Path.GetDirectoryName(Path.GetFullPath(manifestPath))
            ?? throw new InvalidDataException("Manifest has no parent directory.");
        var images = new Dictionary<string, BitmapSource>(StringComparer.Ordinal);
        foreach (var asset in manifest.Assets)
        {
            if (string.IsNullOrWhiteSpace(asset.StableId) || string.IsNullOrWhiteSpace(asset.RuntimePath))
            {
                throw new InvalidDataException("Each asset requires stable_id and runtime_path.");
            }
            var path = Path.GetFullPath(Path.Combine(root, asset.RuntimePath));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Asset {asset.StableId} escapes the bundle directory.");
            }
            if (!File.Exists(path))
            {
                throw new InvalidDataException($"Asset {asset.StableId} runtime file is missing.");
            }
            var actualHash = $"sha256:{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant()}";
            if (!string.Equals(actualHash, asset.RuntimeSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Asset {asset.StableId} SHA-256 does not match the manifest.");
            }

            var image = Decode(path);
            if (!HasVisiblePixel(image))
            {
                throw new InvalidDataException($"Asset {asset.StableId} alpha channel has no visible pixels.");
            }
            images.Add(asset.StableId, image);
        }

        var composition = manifest.Composition;
        RequireImage(images, composition.BodyId);
        RequireImage(images, composition.DefaultExpressionId);
        var body = images[composition.BodyId];
        var expression = images[composition.DefaultExpressionId];
        if (expression.PixelWidth != composition.OverlaySize.Width || expression.PixelHeight != composition.OverlaySize.Height)
        {
            throw new InvalidDataException($"Asset {composition.DefaultExpressionId} does not match overlay_size.");
        }
        if (composition.OverlayOffset.X + composition.OverlaySize.Width > body.PixelWidth
            || composition.OverlayOffset.Y + composition.OverlaySize.Height > body.PixelHeight)
        {
            throw new InvalidDataException("Composition overlay exceeds the body canvas.");
        }

        return new ArtBundle(
            Path.GetFullPath(manifestPath),
            new ArtComposition(
                composition.BodyId,
                composition.DefaultExpressionId,
                new ArtPoint(composition.OverlayOffset.X, composition.OverlayOffset.Y),
                new ArtSize(composition.OverlaySize.Width, composition.OverlaySize.Height),
                new ArtPoint(composition.PanelAnchor.X, composition.PanelAnchor.Y),
                composition.DefaultScale),
            images);
    }

    private static void RequireImage(IReadOnlyDictionary<string, BitmapSource> images, string stableId)
    {
        if (!images.ContainsKey(stableId))
        {
            throw new InvalidDataException($"Composition references missing asset {stableId}.");
        }
    }

    private static BitmapSource Decode(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        frame.Freeze();
        return frame;
    }

    private static bool HasVisiblePixel(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4];
        converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
        for (var index = 3; index < pixels.Length; index += 4)
        {
            if (pixels[index] != 0)
            {
                return true;
            }
        }
        return false;
    }

    private sealed record ManifestDto(
        [property: JsonPropertyName("schema_version")] int SchemaVersion,
        [property: JsonPropertyName("assets")] AssetDto[] Assets,
        [property: JsonPropertyName("composition")] CompositionDto? Composition);

    private sealed record AssetDto(
        [property: JsonPropertyName("stable_id")] string StableId,
        [property: JsonPropertyName("runtime_path")] string RuntimePath,
        [property: JsonPropertyName("runtime_sha256")] string RuntimeSha256);

    private sealed record CompositionDto(
        [property: JsonPropertyName("body_id")] string BodyId,
        [property: JsonPropertyName("default_expression_id")] string DefaultExpressionId,
        [property: JsonPropertyName("overlay_offset")] PointDto OverlayOffset,
        [property: JsonPropertyName("overlay_size")] SizeDto OverlaySize,
        [property: JsonPropertyName("panel_anchor")] PointDto PanelAnchor,
        [property: JsonPropertyName("default_scale")] double DefaultScale);

    private sealed record PointDto(
        [property: JsonPropertyName("x")] int X,
        [property: JsonPropertyName("y")] int Y);

    private sealed record SizeDto(
        [property: JsonPropertyName("width")] int Width,
        [property: JsonPropertyName("height")] int Height);
}
