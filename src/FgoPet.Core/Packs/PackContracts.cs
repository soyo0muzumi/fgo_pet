using System.Text.Json;
using System.Text.Json.Serialization;
using FgoPet.Core.Portraits;

namespace FgoPet.Core.Packs;

/// <summary>
/// Marker for DTOs that capture unknown JSON members and validate cross-field
/// structural invariants during strict deserialization.
/// </summary>
public interface IStrictDeserializable
{
    [JsonExtensionData]
    Dictionary<string, JsonElement>? ExtraData { get; set; }

    void ValidateStructural();
}

public enum ArtAssetKind
{
    Body,
    Expression,
}

/// <summary>One image declared in an appearance manifest (art schema v3).</summary>
public sealed record ArtAssetV3
{
    [JsonPropertyName("type")]
    public required ArtAssetKind AssetType { get; init; }

    [JsonPropertyName("stable_id")]
    public required string StableId { get; init; }

    [JsonPropertyName("path")]
    public required string RelativePath { get; init; }

    [JsonPropertyName("sha256")]
    public required string Sha256 { get; init; }
}

public sealed record PointV3
{
    [JsonPropertyName("x")]
    public required int X { get; init; }

    [JsonPropertyName("y")]
    public required int Y { get; init; }
}

public sealed record SizeV3
{
    [JsonPropertyName("width")]
    public required int Width { get; init; }

    [JsonPropertyName("height")]
    public required int Height { get; init; }
}

/// <summary>Layered portrait geometry for one appearance (art schema v3).</summary>
public sealed record CompositionV3
{
    [JsonPropertyName("body_id")]
    public required string BodyId { get; init; }

    [JsonPropertyName("default_expression_id")]
    public required string DefaultExpressionId { get; init; }

    [JsonPropertyName("overlay_offset")]
    public required PointV3 OverlayOffset { get; init; }

    [JsonPropertyName("overlay_size")]
    public required SizeV3 OverlaySize { get; init; }

    [JsonPropertyName("panel_anchor")]
    public required PointV3 PanelAnchor { get; init; }

    [JsonPropertyName("default_scale")]
    public required double DefaultScale { get; init; }
}

/// <summary>Art manifest (schema v3): images, geometry, and expression semantics.</summary>
public sealed record AppearanceManifestV3 : IStrictDeserializable
{
    [JsonPropertyName("schema_version")]
    public required int SchemaVersion { get; init; }

    [JsonPropertyName("appearance_id")]
    public required string AppearanceId { get; init; }

    [JsonPropertyName("assets")]
    public required IReadOnlyList<ArtAssetV3> Assets { get; init; }

    [JsonPropertyName("composition")]
    public required CompositionV3 Composition { get; init; }

    [JsonPropertyName("expression_semantics")]
    public required IReadOnlyDictionary<string, string> ExpressionSemantics { get; init; }

    [JsonPropertyName("fallback")]
    public IReadOnlyDictionary<string, string> Fallback { get; init; } = new Dictionary<string, string>();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtraData { get; set; }

    public bool HasExpressionAsset(string stableId) =>
        !string.IsNullOrWhiteSpace(stableId)
        && Assets.Any(asset => asset.AssetType == ArtAssetKind.Expression
                               && string.Equals(asset.StableId, stableId, StringComparison.Ordinal));

    public void ValidateStructural()
    {
        if (SchemaVersion != 3)
        {
            throw Failed(PackErrorCode.SchemaUnsupported, $"不支持的 schema_version {SchemaVersion}; 期望 3。");
        }
        if (string.IsNullOrWhiteSpace(AppearanceId))
        {
            throw Failed(PackErrorCode.ManifestMalformed, "appearance_id 不能为空。");
        }
        if (Assets is not { Count: > 0 })
        {
            throw Failed(PackErrorCode.ManifestMalformed, "外观必须包含至少一个素材。");
        }

        var duplicates = Assets
            .GroupBy(asset => asset.StableId, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();
        if (duplicates.Count > 0)
        {
            throw Failed(PackErrorCode.ManifestMalformed, $"重复的 stable_id: {string.Join(", ", duplicates)}。");
        }

        if (!Assets.Any(asset => asset.AssetType == ArtAssetKind.Body
                                 && string.Equals(asset.StableId, Composition.BodyId, StringComparison.Ordinal)))
        {
            throw Failed(PackErrorCode.ManifestMalformed, $"composition.body_id '{Composition.BodyId}' 未指向 body 素材。");
        }
        if (!HasExpressionAsset(Composition.DefaultExpressionId))
        {
            throw Failed(PackErrorCode.ManifestMalformed, $"composition.default_expression_id '{Composition.DefaultExpressionId}' 未指向 expression 素材。");
        }

        var scale = Composition.DefaultScale;
        if (scale is not 0.50 and not 0.60 and not 0.75)
        {
            throw Failed(PackErrorCode.ManifestMalformed, $"不支持的 default_scale {scale:R}; 仅允许 0.50/0.60/0.75。");
        }
        if (Composition.OverlayOffset.X < 0 || Composition.OverlayOffset.Y < 0)
        {
            throw Failed(PackErrorCode.ManifestMalformed, "overlay_offset 不能为负。");
        }
        if (Composition.OverlaySize.Width <= 0 || Composition.OverlaySize.Height <= 0)
        {
            throw Failed(PackErrorCode.ManifestMalformed, "overlay_size 必须为正。");
        }
        if (Composition.PanelAnchor.X < 0 || Composition.PanelAnchor.Y < 0)
        {
            throw Failed(PackErrorCode.ManifestMalformed, "panel_anchor 不能为负。");
        }

        foreach (var semantic in ExpressionSemanticKeys.Core)
        {
            if (!ExpressionSemantics.ContainsKey(semantic))
            {
                throw Failed(PackErrorCode.ExpressionMappingInvalid, $"缺少核心表情语义 '{semantic}'。");
            }
        }
        foreach (var (key, value) in ExpressionSemantics)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw Failed(PackErrorCode.ExpressionMappingInvalid, $"表情语义 '{key}' 映射到空素材 ID。");
            }
        }
    }

    private static PackFailureException Failed(PackErrorCode code, string message)
        => new(new PackFailure(code, message, null));
}

/// <summary>One appearance entry inside a pack manifest (pack schema v1).</summary>
public sealed record PackAppearanceRef
{
    [JsonPropertyName("appearance_id")]
    public required string AppearanceId { get; init; }

    [JsonPropertyName("manifest_path")]
    public required string ManifestPath { get; init; }
}

/// <summary>Pack manifest (pack schema v1): package identity, appearances, and preview.</summary>
public sealed record PackManifestV1 : IStrictDeserializable
{
    [JsonPropertyName("schema_version")]
    public required int SchemaVersion { get; init; }

    [JsonPropertyName("package_id")]
    public required string PackageId { get; init; }

    [JsonPropertyName("package_version")]
    public required string PackageVersion { get; init; }

    [JsonPropertyName("servant_id")]
    public required string ServantId { get; init; }

    [JsonPropertyName("display_name")]
    public string DisplayName { get; init; } = string.Empty;

    [JsonPropertyName("publisher")]
    public string Publisher { get; init; } = string.Empty;

    [JsonPropertyName("min_app_version")]
    public string MinAppVersion { get; init; } = string.Empty;

    [JsonPropertyName("preview_path")]
    public string PreviewPath { get; init; } = string.Empty;

    [JsonPropertyName("appearances")]
    public required IReadOnlyList<PackAppearanceRef> Appearances { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtraData { get; set; }

    public void ValidateStructural()
    {
        if (SchemaVersion != 1)
        {
            throw new PackFailureException(new PackFailure(PackErrorCode.SchemaUnsupported, $"不支持的 schema_version {SchemaVersion}; 期望 1。"));
        }
        if (string.IsNullOrWhiteSpace(PackageId))
        {
            throw new PackFailureException(new PackFailure(PackErrorCode.ManifestMalformed, "package_id 不能为空。"));
        }
        if (!SemVersion.TryParse(PackageVersion, out _))
        {
            throw new PackFailureException(new PackFailure(PackErrorCode.ManifestMalformed, $"package_version '{PackageVersion}' 不是有效的 SemVer。"));
        }
        if (string.IsNullOrWhiteSpace(ServantId))
        {
            throw new PackFailureException(new PackFailure(PackErrorCode.ManifestMalformed, "servant_id 不能为空。"));
        }
        if (string.IsNullOrWhiteSpace(PreviewPath))
        {
            throw new PackFailureException(new PackFailure(PackErrorCode.ManifestMalformed, "preview_path 不能为空。"));
        }
        if (Appearances is null or { Count: 0 })
        {
            throw new PackFailureException(new PackFailure(PackErrorCode.ManifestMalformed, "包必须声明至少一个外观。"));
        }

        var duplicates = Appearances
            .GroupBy(appearance => appearance.AppearanceId, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();
        if (duplicates.Count > 0)
        {
            throw new PackFailureException(new PackFailure(PackErrorCode.ManifestMalformed, $"重复的 appearance_id: {string.Join(", ", duplicates)}。"));
        }
    }
}