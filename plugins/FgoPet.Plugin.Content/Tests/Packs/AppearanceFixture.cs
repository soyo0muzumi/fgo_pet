using FgoPet.Core.Packs;
using FgoPet.Core.Portraits;

namespace FgoPet.Core.Tests.Packs;

internal static class AppearanceFixture
{
    private const string Sha256 = "sha256:0000000000000000000000000000000000000000000000000000000000000000";

    public static AppearanceManifestV3 Appearance(
        IReadOnlyDictionary<string, string> mapping,
        IReadOnlyDictionary<string, string>? fallback = null,
        IEnumerable<string>? expressionIds = null)
    {
        var ids = (expressionIds ?? new[] { "face01", "face02", "face03" }).ToArray();
        var assets = new List<ArtAssetV3>
        {
            new()
            {
                AssetType = ArtAssetKind.Body,
                StableId = "full_body",
                RelativePath = "runtime/full_body.png",
                Sha256 = Sha256,
            },
        };
        for (var index = 0; index < ids.Length; index++)
        {
            assets.Add(new ArtAssetV3
            {
                AssetType = ArtAssetKind.Expression,
                StableId = ids[index],
                RelativePath = $"runtime/expressions/{ids[index]}.png",
                Sha256 = Sha256,
            });
        }

        return new AppearanceManifestV3
        {
            SchemaVersion = 3,
            AppearanceId = "fixture",
            Assets = assets,
            Composition = new CompositionV3
            {
                BodyId = "full_body",
                DefaultExpressionId = ids[0],
                OverlayOffset = new PointV3 { X = 13, Y = 0 },
                OverlaySize = new SizeV3 { Width = 256, Height = 240 },
                PanelAnchor = new PointV3 { X = 151, Y = 360 },
                DefaultScale = 0.50,
            },
            ExpressionSemantics = new Dictionary<string, string>(mapping, StringComparer.Ordinal),
            Fallback = fallback ?? new Dictionary<string, string>(StringComparer.Ordinal),
        };
    }
}