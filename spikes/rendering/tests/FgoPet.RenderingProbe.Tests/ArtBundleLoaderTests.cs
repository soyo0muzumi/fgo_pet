using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FgoPet.RenderingProbe.Art;

namespace FgoPet.RenderingProbe.Tests;

public sealed class ArtBundleLoaderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"fgo-pet-probe-{Guid.NewGuid():N}");

    public ArtBundleLoaderTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void Load_reads_schema_v2_composition_and_freezes_images()
    {
        var manifestPath = WriteBundle();

        var bundle = ArtBundleLoader.Load(manifestPath);

        Assert.Equal("full_body", bundle.Composition.BodyId);
        Assert.Equal("r01c01", bundle.Composition.DefaultExpressionId);
        Assert.Equal(24, bundle.Composition.OverlayOffset.X);
        Assert.Equal(0.6, bundle.Composition.DefaultScale);
        Assert.True(bundle.Images.Values.All(image => image.IsFrozen));
    }

    [Fact]
    public void Load_rejects_schema_without_composition()
    {
        var manifestPath = WriteBundle();
        var document = JsonSerializer.Deserialize<Dictionary<string, object?>>(File.ReadAllText(manifestPath))!;
        document.Remove("composition");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(document));

        var error = Assert.Throws<InvalidDataException>(() => ArtBundleLoader.Load(manifestPath));

        Assert.Contains("composition", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_rejects_hash_mismatch_with_stable_id()
    {
        var manifestPath = WriteBundle();
        File.AppendAllText(Path.Combine(_directory, "runtime", "full_body.png"), "damage");

        var error = Assert.Throws<InvalidDataException>(() => ArtBundleLoader.Load(manifestPath));

        Assert.Contains("full_body", error.Message);
        Assert.Contains("SHA-256", error.Message);
    }

    [Fact]
    public void Load_rejects_missing_runtime_file_with_stable_id()
    {
        var manifestPath = WriteBundle();
        File.Delete(Path.Combine(_directory, "runtime", "expressions", "r01c01.png"));

        var error = Assert.Throws<InvalidDataException>(() => ArtBundleLoader.Load(manifestPath));

        Assert.Contains("r01c01", error.Message);
        Assert.Contains("missing", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_rejects_fully_transparent_runtime_image()
    {
        var manifestPath = WriteBundle(expressionAlpha: 0);

        var error = Assert.Throws<InvalidDataException>(() => ArtBundleLoader.Load(manifestPath));

        Assert.Contains("r01c01", error.Message);
        Assert.Contains("alpha", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_rejects_missing_default_expression_id()
    {
        var manifestPath = WriteBundle();
        var root = JsonNode.Parse(File.ReadAllText(manifestPath))!;
        root["composition"]!["default_expression_id"] = "r07c04";
        File.WriteAllText(manifestPath, root.ToJsonString());

        var error = Assert.Throws<InvalidDataException>(() => ArtBundleLoader.Load(manifestPath));

        Assert.Contains("r07c04", error.Message);
    }

    private string WriteBundle(byte expressionAlpha = 255)
    {
        Directory.CreateDirectory(Path.Combine(_directory, "runtime", "expressions"));
        var bodyPath = Path.Combine(_directory, "runtime", "full_body.png");
        var expressionPath = Path.Combine(_directory, "runtime", "expressions", "r01c01.png");
        WritePng(bodyPath, 303, 603);
        WritePng(expressionPath, 256, 240, expressionAlpha);
        var manifest = new
        {
            schema_version = 2,
            assets = new[]
            {
                new { stable_id = "full_body", runtime_path = "runtime/full_body.png", runtime_sha256 = Hash(bodyPath) },
                new { stable_id = "r01c01", runtime_path = "runtime/expressions/r01c01.png", runtime_sha256 = Hash(expressionPath) },
            },
            composition = new
            {
                body_id = "full_body",
                default_expression_id = "r01c01",
                overlay_offset = new { x = 24, y = 0 },
                overlay_size = new { width = 256, height = 240 },
                panel_anchor = new { x = 151, y = 360 },
                default_scale = 0.6,
            },
        };
        var path = Path.Combine(_directory, "manifest.json");
        File.WriteAllText(path, JsonSerializer.Serialize(manifest));
        return path;
    }

    private static void WritePng(string path, int width, int height, byte alpha = 255)
    {
        var pixels = new byte[width * height * 4];
        for (var index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = 255;
            pixels[index + 1] = 255;
            pixels[index + 2] = 255;
            pixels[index + 3] = alpha;
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static string Hash(string path) => $"sha256:{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant()}";

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
