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

/// <summary>Application-owned declarative field types permitted in package settings.</summary>
public enum PackSettingType
{
    Toggle,
    Choice,
    Text,
}

/// <summary>One declarative package setting rendered only by application-owned controls.</summary>
public sealed record PackSettingDefinition
{
    [JsonPropertyName("key")]
    public required string Key { get; init; }

    [JsonPropertyName("label")]
    public required string Label { get; init; }

    [JsonPropertyName("type")]
    public required PackSettingType Type { get; init; }

    [JsonPropertyName("default")]
    public required string Default { get; init; }

    [JsonPropertyName("options")]
    public IReadOnlyList<string>? Options { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtraData { get; set; }
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

    [JsonPropertyName("settings")]
    public IReadOnlyList<PackSettingDefinition> Settings { get; init; } = Array.Empty<PackSettingDefinition>();

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

        ValidateSettings();
    }

    private void ValidateSettings()
    {
        if (Settings is null || Settings.Count > 32)
        {
            throw Malformed("包最多可声明 32 个设置。");
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var setting in Settings)
        {
            if (setting is null)
            {
                throw Malformed("设置定义不能为空。");
            }
            if (setting.ExtraData is { Count: > 0 })
            {
                throw Malformed($"设置 '{setting.Key}' 存在未知属性。");
            }
            if (!IsValidSettingKey(setting.Key))
            {
                throw Malformed("设置 key 必须是 1–64 个小写 ASCII 字符，且只能包含字母、数字、点、下划线或连字符。");
            }
            if (setting.Label is null || setting.Label != setting.Label.Trim() || setting.Label.Length is < 1 or > 80)
            {
                throw Malformed($"设置 '{setting.Key}' 的 label 必须是 1–80 个非空白字符。");
            }
            if (!keys.Add(setting.Key))
            {
                throw Malformed($"重复的设置 key: {setting.Key}。");
            }

            switch (setting.Type)
            {
                case PackSettingType.Toggle:
                    if (setting.Default is not "true" and not "false" || setting.Options is not null)
                    {
                        throw Malformed($"开关设置 '{setting.Key}' 必须使用 true 或 false 默认值，且不能包含 options。");
                    }
                    break;

                case PackSettingType.Choice:
                    ValidateChoice(setting);
                    break;

                case PackSettingType.Text:
                    if (setting.Default is null || setting.Default.Length > 256 || setting.Options is not null)
                    {
                        throw Malformed($"文本设置 '{setting.Key}' 的默认值最长为 256 个字符，且不能包含 options。");
                    }
                    break;

                default:
                    throw Malformed($"不支持的设置类型 '{setting.Type}'。");
            }
        }
    }

    private static void ValidateChoice(PackSettingDefinition setting)
    {
        if (setting.Options is not { Count: >= 2 and <= 20 })
        {
            throw Malformed($"选择设置 '{setting.Key}' 必须包含 2–20 个选项。");
        }
        if (setting.Options.Any(option => string.IsNullOrWhiteSpace(option) || option.Length > 64))
        {
            throw Malformed($"选择设置 '{setting.Key}' 的选项必须是 1–64 个字符。");
        }
        if (setting.Options.Distinct(StringComparer.Ordinal).Count() != setting.Options.Count)
        {
            throw Malformed($"选择设置 '{setting.Key}' 的选项不能重复。");
        }
        if (!setting.Options.Contains(setting.Default, StringComparer.Ordinal))
        {
            throw Malformed($"选择设置 '{setting.Key}' 的默认值必须属于 options。");
        }
    }

    private static bool IsValidSettingKey(string? key)
    {
        if (key is null || key != key.Trim() || key.Length is < 1 or > 64)
        {
            return false;
        }

        var first = key[0];
        if (!((first >= 'a' && first <= 'z') || (first >= '0' && first <= '9')))
        {
            return false;
        }

        return key.All(character => character is >= 'a' and <= 'z'
                                    or >= '0' and <= '9'
                                    or '.' or '_' or '-');
    }

    private static PackFailureException Malformed(string message) =>
        new(new PackFailure(PackErrorCode.ManifestMalformed, message));
}
