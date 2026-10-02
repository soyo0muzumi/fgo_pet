using System.Text.Json;
using System.Text.Json.Nodes;
using FgoPet.Core.Packs;
using FgoPet.Core.Portraits;
using Xunit;

namespace FgoPet.Core.Tests.Packs;

public sealed class PackContractTests
{
    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", "packs", name);

    [Fact]
    public void Mash_v3_fixture_deserializes_strict()
    {
        var json = File.ReadAllText(FixturePath("mash-art-v3.json"));
        var manifest = PackJson.DeserializeStrict<AppearanceManifestV3>(json);

        Assert.Equal(3, manifest.SchemaVersion);
        Assert.Equal("casual", manifest.AppearanceId);
        Assert.Equal(29, manifest.Assets.Count);
        Assert.Equal(ArtAssetKind.Body, manifest.Assets.Single(asset => asset.StableId == "full_body").AssetType);
        Assert.Equal(28, manifest.Assets.Count(asset => asset.AssetType == ArtAssetKind.Expression));
        Assert.Equal(new PointV3 { X = 13, Y = 0 }, manifest.Composition.OverlayOffset);
        Assert.Equal(new SizeV3 { Width = 256, Height = 240 }, manifest.Composition.OverlaySize);
        Assert.Equal(new PointV3 { X = 151, Y = 360 }, manifest.Composition.PanelAnchor);
        Assert.Equal(0.50, manifest.Composition.DefaultScale);
        Assert.Equal(
            ExpressionSemanticKeys.Core.OrderBy(key => key, StringComparer.Ordinal),
            manifest.ExpressionSemantics.Keys.OrderBy(key => key, StringComparer.Ordinal));
    }

    [Fact]
    public void Mash_v2_fixture_preserves_ids_and_geometry()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(FixturePath("mash-art-v2.json")));
        var root = json.RootElement;
        Assert.Equal(2, root.GetProperty("schema_version").GetInt32());

        var ids = root.GetProperty("assets")
            .EnumerateArray()
            .Select(asset => asset.GetProperty("stable_id").GetString())
            .ToArray();
        Assert.Equal(29, ids.Length);
        Assert.Contains("full_body", ids);
        Assert.Equal(28, ids.Count(id => id is not null && id.Length == 6 && id.StartsWith("r", StringComparison.Ordinal)));

        var composition = root.GetProperty("composition");
        Assert.Equal(13, composition.GetProperty("overlay_offset").GetProperty("x").GetInt32());
        Assert.Equal(0, composition.GetProperty("overlay_offset").GetProperty("y").GetInt32());
        Assert.Equal(256, composition.GetProperty("overlay_size").GetProperty("width").GetInt32());
        Assert.Equal(240, composition.GetProperty("overlay_size").GetProperty("height").GetInt32());
        Assert.Equal(151, composition.GetProperty("panel_anchor").GetProperty("x").GetInt32());
        Assert.Equal(360, composition.GetProperty("panel_anchor").GetProperty("y").GetInt32());
        Assert.Equal(0.5, composition.GetProperty("default_scale").GetDouble());
    }

    [Fact]
    public void Unknown_json_property_fails_deserialization()
    {
        var json = File.ReadAllText(FixturePath("mash-art-v3.json"));
        var injected = Inject(json, json =>
        {
            json["unexpected_field"] = true;
        });

        var failure = Assert.Throws<PackFailureException>(() =>
            PackJson.DeserializeStrict<AppearanceManifestV3>(injected));
        Assert.Equal(PackErrorCode.ManifestMalformed, failure.Failure.Code);
    }

    [Fact]
    public void Duplicate_stable_ids_fail()
    {
        var injected = V3Json(json =>
        {
            var assets = json["assets"]!.AsArray();
            var duplicate = assets[0]!.DeepClone();
            assets.Add(duplicate);
        });

        var failure = Assert.Throws<PackFailureException>(() =>
            PackJson.DeserializeStrict<AppearanceManifestV3>(injected));
        Assert.Equal(PackErrorCode.ManifestMalformed, failure.Failure.Code);
    }

    [Fact]
    public void Unsupported_default_scale_fails()
    {
        var injected = V3Json(json => json["composition"]!["default_scale"] = 0.7);

        var failure = Assert.Throws<PackFailureException>(() =>
            PackJson.DeserializeStrict<AppearanceManifestV3>(injected));
        Assert.Equal(PackErrorCode.ManifestMalformed, failure.Failure.Code);
    }

    [Fact]
    public void Missing_core_semantic_fails()
    {
        var injected = V3Json(json =>
        {
            json["expression_semantics"]!.AsObject().Remove("angry");
            json["fallback"]!.AsObject().Remove("angry");
        });

        var failure = Assert.Throws<PackFailureException>(() =>
            PackJson.DeserializeStrict<AppearanceManifestV3>(injected));
        Assert.Equal(PackErrorCode.ExpressionMappingInvalid, failure.Failure.Code);
    }

    [Fact]
    public void PackManifestV1_requires_schema_v1_and_rejects_unknown()
    {
        var json = """
        {
          "schema_version": 1,
          "package_id": "official.mash",
          "package_version": "1.0.0",
          "servant_id": "mash_kyrielight",
          "display_name": "玛修·基列莱特",
          "publisher": "community",
          "preview_path": "previews/library.png",
          "appearances": [
            { "appearance_id": "casual", "manifest_path": "appearances/casual/manifest.json" }
          ]
        }
        """;

        var manifest = PackJson.DeserializeStrict<PackManifestV1>(json);
        Assert.Equal("official.mash", manifest.PackageId);
        Assert.Equal("mash_kyrielight", manifest.ServantId);
        Assert.Single(manifest.Appearances);

        var withUnknown = json.Replace("\"preview_path\"", "\"surprise\": 1, \"preview_path\"", StringComparison.Ordinal);
        var failure = Assert.Throws<PackFailureException>(() =>
            PackJson.DeserializeStrict<PackManifestV1>(withUnknown));
        Assert.Equal(PackErrorCode.ManifestMalformed, failure.Failure.Code);
    }

    [Fact]
    public void Shared_minimal_fixture_round_trips_capabilities_and_declared_files()
    {
        var json = File.ReadAllText(FixturePath("valid-minimal/package.json"));

        var manifest = PackJson.DeserializeStrict<PackManifestV1>(json);

        Assert.Equal(["art.v3"], manifest.Capabilities);
        Assert.Contains("previews/library.png", manifest.Files);
        Assert.Contains("appearances/default/manifest.json", manifest.Files);
    }

    [Fact]
    public void Unknown_capability_in_shared_fixture_is_rejected()
    {
        var json = File.ReadAllText(FixturePath("invalid-cases/unknown-capability/package.json"));

        var failure = Assert.Throws<PackFailureException>(() =>
            PackJson.DeserializeStrict<PackManifestV1>(json));

        Assert.Equal(PackErrorCode.ManifestMalformed, failure.Failure.Code);
    }

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("1.0.0-alpha.1")]
    [InlineData("10.2.3")]
    public void SemVersion_parses_valid_versions(string text)
    {
        Assert.True(SemVersion.TryParse(text, out var version));
        Assert.Equal(text, version.ToString());
    }

    [Fact]
    public void SemVersion_orders_release_before_prerelease()
    {
        Assert.True(SemVersion.Parse("1.0.0").CompareTo(SemVersion.Parse("1.0.0-alpha")) > 0);
        Assert.True(SemVersion.Parse("1.0.0-alpha.1").CompareTo(SemVersion.Parse("1.0.0-beta")) < 0);
    }

    private static string V3Json(Action<JsonObject> mutate)
    {
        var node = JsonNode.Parse(File.ReadAllText(FixturePath("mash-art-v3.json")))!.AsObject();
        mutate(node);
        return node.ToJsonString();
    }

    private static string Inject(string json, Action<JsonObject> mutate)
    {
        var node = JsonNode.Parse(json)!.AsObject();
        mutate(node);
        return node.ToJsonString();
    }
}
