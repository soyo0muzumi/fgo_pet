using System.Text;
using System.Text.Json.Nodes;
using FgoPet.Core.Packs;
using FgoPet.Infrastructure.Packs;
using Xunit;

namespace FgoPet.Infrastructure.Tests.Packs;

public sealed class Live2DAppearanceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fgo-pet-live2d-" + Guid.NewGuid().ToString("N"));

    public Live2DAppearanceTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Declared_model_and_resources_validate_with_static_fallback()
    {
        var manifest = CreateManifest();

        var result = AppearanceValidator.Validate(manifest, _root);

        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(error => error.Message)));
        Assert.NotNull(result.Value!.Manifest.Live2D);
    }

    [Fact]
    public void Model_reference_missing_from_declarations_is_rejected()
    {
        var manifest = CreateManifest(node => node["live2d"]!["files"]!.AsArray().RemoveAt(2));

        var result = AppearanceValidator.Validate(manifest, _root);

        Assert.False(result.IsValid);
        Assert.Equal(PackErrorCode.ManifestMalformed, result.Errors[0].Code);
    }

    [Fact]
    public void Traversal_in_model_reference_is_rejected()
    {
        var manifest = CreateManifest(modelTexture: "../outside.png");

        var result = AppearanceValidator.Validate(manifest, _root);

        Assert.False(result.IsValid);
        Assert.Equal(PackErrorCode.PackagePathEscapesRoot, result.Errors[0].Code);
    }

    [Fact]
    public void Tampered_moc_is_rejected()
    {
        var manifest = CreateManifest();
        File.WriteAllBytes(Path.Combine(_root, "live2d", "mash.moc3"), "tampered"u8.ToArray());

        var result = AppearanceValidator.Validate(manifest, _root);

        Assert.False(result.IsValid);
        Assert.Equal(PackErrorCode.AssetHashMismatch, result.Errors[0].Code);
        Assert.True(AppearanceValidator.ValidateStatic(manifest, _root).IsValid);
    }

    [Fact]
    public void Executable_resource_declaration_is_rejected()
    {
        var manifest = CreateManifest(node => node["live2d"]!["files"]!.AsArray().Add(new JsonObject
        {
            ["path"] = "live2d/inject.js", ["sha256"] = PackFixture.Sha256("x"u8.ToArray()),
        }));

        var result = AppearanceValidator.Validate(manifest, _root);

        Assert.False(result.IsValid);
        Assert.Equal(PackErrorCode.ManifestMalformed, result.Errors[0].Code);
    }

    private AppearanceManifestV3 CreateManifest(Action<JsonObject>? edit = null, string modelTexture = "texture.png")
    {
        var body = "body"u8.ToArray();
        var expression = "expression"u8.ToArray();
        var modelNode = JsonNode.Parse("""{"Version":3,"FileReferences":{"Moc":"mash.moc3","Textures":["texture.png"],"Motions":{"Idle":[{"File":"idle.motion3.json"}]}}}""")!;
        modelNode["FileReferences"]!["Textures"]![0] = modelTexture;
        var model = Encoding.UTF8.GetBytes(modelNode.ToJsonString());
        var moc = "MOC3"u8.ToArray();
        var texture = "png"u8.ToArray();
        var motion = "{}"u8.ToArray();
        Write("runtime/body.png", body);
        Write("runtime/expression.png", expression);
        Write("live2d/mash.model3.json", model);
        Write("live2d/mash.moc3", moc);
        Write("live2d/texture.png", texture);
        Write("live2d/idle.motion3.json", motion);
        var node = JsonNode.Parse(PackFixture.V3Json([
            ("body", "full_body", "runtime/body.png", PackFixture.Sha256(body)),
            ("expression", "neutral", "runtime/expression.png", PackFixture.Sha256(expression)),
        ]))!.AsObject();
        node["live2d"] = new JsonObject
        {
            ["model_path"] = "live2d/mash.model3.json",
            ["files"] = new JsonArray
            {
                new JsonObject { ["path"] = "live2d/mash.model3.json", ["sha256"] = PackFixture.Sha256(model) },
                new JsonObject { ["path"] = "live2d/mash.moc3", ["sha256"] = PackFixture.Sha256(moc) },
                new JsonObject { ["path"] = "live2d/texture.png", ["sha256"] = PackFixture.Sha256(texture) },
                new JsonObject { ["path"] = "live2d/idle.motion3.json", ["sha256"] = PackFixture.Sha256(motion) },
            },
        };
        edit?.Invoke(node);
        return PackJson.DeserializeStrict<AppearanceManifestV3>(node.ToJsonString());
    }

    private void Write(string relative, byte[] content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }
}
