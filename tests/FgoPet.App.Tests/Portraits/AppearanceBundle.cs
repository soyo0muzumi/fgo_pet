using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FgoPet.Core.Portraits;

namespace FgoPet.App.Tests.Portraits;

/// <summary>
/// Generates synthetic PNG assets and art schema v3 manifests in a temp root.
/// No real FGO art is used.
/// </summary>
internal static class AppearanceBundle
{
    public static string Sha256(byte[] content) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    public static byte[] CreatePng(int width, int height, byte alpha)
    {
        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        var pixels = new byte[width * height * 4];
        for (var index = 0; index < width * height; index++)
        {
            pixels[(index * 4) + 0] = 40;
            pixels[(index * 4) + 1] = 90;
            pixels[(index * 4) + 2] = 160;
            pixels[(index * 4) + 3] = alpha;
        }
        bitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    public static (string Root, string ManifestPath) Write(
        string root,
        byte[] bodyPng,
        byte[] expressionPng,
        int bodyWidth = 303,
        int bodyHeight = 603,
        int overlayWidth = 256,
        int overlayHeight = 240,
        int overlayX = 13,
        int overlayY = 0,
        int panelX = 151,
        int panelY = 360)
    {
        Directory.CreateDirectory(Path.Combine(root, "runtime", "expressions"));
        File.WriteAllBytes(Path.Combine(root, "runtime", "full_body.png"), bodyPng);
        File.WriteAllBytes(Path.Combine(root, "runtime", "expressions", "r01c01.png"), expressionPng);

        var json = V3Json(
            bodyWidth, bodyHeight,
            overlayWidth, overlayHeight, overlayX, overlayY, panelX, panelY,
            Sha256(bodyPng), Sha256(expressionPng));
        var manifestPath = Path.Combine(root, "manifest.json");
        File.WriteAllText(manifestPath, json);
        return (root, manifestPath);
    }

    private static string V3Json(
        int bodyWidth, int bodyHeight,
        int overlayWidth, int overlayHeight, int overlayX, int overlayY,
        int panelX, int panelY,
        string bodyHash, string expressionHash)
    {
        var semantics = new JsonObject();
        foreach (var key in ExpressionSemanticKeys.Core)
        {
            semantics[key] = "r01c01";
        }
        var fallback = new JsonObject();
        foreach (var key in ExpressionSemanticKeys.Core.Where(key => key != ExpressionSemanticKeys.Neutral))
        {
            fallback[key] = ExpressionSemanticKeys.Neutral;
        }

        var root = new JsonObject
        {
            ["schema_version"] = 3,
            ["appearance_id"] = "casual",
            ["assets"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "body",
                    ["stable_id"] = "full_body",
                    ["path"] = "runtime/full_body.png",
                    ["sha256"] = bodyHash,
                },
                new JsonObject
                {
                    ["type"] = "expression",
                    ["stable_id"] = "r01c01",
                    ["path"] = "runtime/expressions/r01c01.png",
                    ["sha256"] = expressionHash,
                },
            },
            ["composition"] = new JsonObject
            {
                ["body_id"] = "full_body",
                ["default_expression_id"] = "r01c01",
                ["overlay_offset"] = new JsonObject { ["x"] = overlayX, ["y"] = overlayY },
                ["overlay_size"] = new JsonObject { ["width"] = overlayWidth, ["height"] = overlayHeight },
                ["panel_anchor"] = new JsonObject { ["x"] = panelX, ["y"] = panelY },
                ["default_scale"] = 0.5,
            },
            ["expression_semantics"] = semantics,
            ["fallback"] = fallback,
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}