using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using FgoPet.Core.Packs;
using FgoPet.Core.Portraits;

namespace FgoPet.Infrastructure.Tests.Packs;

/// <summary>Builds synthetic art schema v3 assets and manifests; no real FGO art is used.</summary>
internal static class PackFixture
{
    public static string Sha256(byte[] content) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    public static string V3Json(
        IEnumerable<(string Type, string StableId, string Path, string Hash)> assets,
        int bodyWidth = 303,
        int bodyHeight = 603,
        int overlayWidth = 256,
        int overlayHeight = 240,
        int overlayX = 13,
        int overlayY = 0,
        int panelX = 151,
        int panelY = 360)
    {
        var list = assets.ToList();
        var expressionId = list.First(asset => asset.Type == "expression").StableId;
        var assetsNode = new JsonArray();
        foreach (var asset in list)
        {
            assetsNode.Add(new JsonObject
            {
                ["type"] = asset.Type,
                ["stable_id"] = asset.StableId,
                ["path"] = asset.Path,
                ["sha256"] = asset.Hash,
            });
        }

        var semantics = new JsonObject();
        foreach (var key in ExpressionSemanticKeys.Core)
        {
            semantics[key] = expressionId;
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
            ["assets"] = assetsNode,
            ["composition"] = new JsonObject
            {
                ["body_id"] = "full_body",
                ["default_expression_id"] = expressionId,
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