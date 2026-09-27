using System.Text;
using FgoPet.Core.Packs;
using FgoPet.Infrastructure.Packs;
using Xunit;

namespace FgoPet.Infrastructure.Tests.Packs;

public sealed class AppearanceValidatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fgo-pet-validate-" + Guid.NewGuid().ToString("N"));

    public AppearanceValidatorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // best effort cleanup
        }
    }

    [Fact]
    public void Validate_passes_a_complete_bundle()
    {
        var manifest = WriteDummyBundle();

        var result = AppearanceValidator.Validate(manifest, _root);

        Assert.True(result.IsValid);
        Assert.NotNull(result.Value);
        Assert.Equal(Path.GetFullPath(_root), result.Value!.Root);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validate_accepts_sha256_without_prefix()
    {
        var bodyContent = Encoding.UTF8.GetBytes("body-bytes");
        WriteAsset(bodyContent, "runtime/full_body.png");
        var manifest = PackFixture.V3Json([
            ("body", "full_body", "runtime/full_body.png", PackFixture.Sha256(bodyContent)["sha256:".Length..]),
            ("expression", "r01c01", "runtime/expressions/r01c01.png", PackFixture.Sha256(Encoding.UTF8.GetBytes("expr"))),
        ]);
        WriteAsset(Encoding.UTF8.GetBytes("expr"), "runtime/expressions/r01c01.png");

        var result = AppearanceValidator.Validate(AppearanceManifestReader.Read(WriteManifest(manifest)), _root);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_rejects_an_absolute_asset_path()
    {
        WriteAsset(Encoding.UTF8.GetBytes("expr-bytes"), "runtime/expressions/r01c01.png");
        var manifest = Parse(PackFixture.V3Json([
            ("body", "full_body", @"C:\\payload\\full_body.png", "sha256:00"),
            ("expression", "r01c01", "runtime/expressions/r01c01.png", PackFixture.Sha256(Encoding.UTF8.GetBytes("expr-bytes"))),
        ]));

        var result = AppearanceValidator.Validate(manifest, _root);

        Assert.False(result.IsValid);
        var failure = Assert.Single(result.Errors);
        Assert.Equal(PackErrorCode.PackagePathEscapesRoot, failure.Code);
    }

    [Fact]
    public void Validate_rejects_a_traversal_asset_path()
    {
        WriteAsset(Encoding.UTF8.GetBytes("expr-bytes"), "runtime/expressions/r01c01.png");
        var manifest = Parse(PackFixture.V3Json([
            ("body", "full_body", "../../outside/full_body.png", "sha256:00"),
            ("expression", "r01c01", "runtime/expressions/r01c01.png", PackFixture.Sha256(Encoding.UTF8.GetBytes("expr-bytes"))),
        ]));

        var result = AppearanceValidator.Validate(manifest, _root);

        Assert.False(result.IsValid);
        var failure = Assert.Single(result.Errors);
        Assert.Equal(PackErrorCode.PackagePathEscapesRoot, failure.Code);
        Assert.Equal("../../outside/full_body.png", failure.RelativePath);
    }

    [Fact]
    public void Validate_reports_a_missing_file_as_AssetMissing()
    {
        var missing = PackFixture.Sha256(Encoding.UTF8.GetBytes("body"));
        WriteAsset(Encoding.UTF8.GetBytes("body"), "runtime/full_body.png");
        var manifest = Parse(PackFixture.V3Json([
            ("body", "full_body", "runtime/full_body.png", missing),
            ("expression", "r01c01", "runtime/missing.png", PackFixture.Sha256(Encoding.UTF8.GetBytes("expr"))),
        ]));

        var result = AppearanceValidator.Validate(manifest, _root);

        Assert.False(result.IsValid);
        var failure = Assert.Single(result.Errors);
        Assert.Equal(PackErrorCode.AssetMissing, failure.Code);
        Assert.Equal("runtime/missing.png", failure.RelativePath);
    }

    [Fact]
    public void Validate_reports_a_hash_mismatch_as_AssetHashMismatch()
    {
        WriteAsset(Encoding.UTF8.GetBytes("actual-body"), "runtime/full_body.png");
        WriteAsset(Encoding.UTF8.GetBytes("actual-expr"), "runtime/expressions/r01c01.png");
        var manifest = Parse(PackFixture.V3Json([
            ("body", "full_body", "runtime/full_body.png", PackFixture.Sha256(Encoding.UTF8.GetBytes("declared-body"))),
            ("expression", "r01c01", "runtime/expressions/r01c01.png", PackFixture.Sha256(Encoding.UTF8.GetBytes("actual-expr"))),
        ]));

        var result = AppearanceValidator.Validate(manifest, _root);

        Assert.False(result.IsValid);
        var failure = Assert.Single(result.Errors);
        Assert.Equal(PackErrorCode.AssetHashMismatch, failure.Code);
        Assert.Equal("runtime/full_body.png", failure.RelativePath);
    }

    [Fact]
    public void Validate_reports_every_failing_asset()
    {
        var manifest = Parse(PackFixture.V3Json([
            ("body", "full_body", "missing/full_body.png", "sha256:00"),
            ("expression", "r01c01", "runtime/expressions/r01c01.png", "sha256:00"),
            ("expression", "r01c02", "runtime/expressions/r01c02.png", "sha256:00"),
        ]));

        var result = AppearanceValidator.Validate(manifest, _root);

        Assert.False(result.IsValid);
        Assert.Equal(3, result.Errors.Count);
        Assert.All(result.Errors, failure => Assert.Equal(PackErrorCode.AssetMissing, failure.Code));
    }

    private static AppearanceManifestV3 Parse(string json) =>
        PackJson.DeserializeStrict<AppearanceManifestV3>(json);

    private AppearanceManifestV3 WriteDummyBundle()
    {
        WriteAsset(Encoding.UTF8.GetBytes("body-bytes"), "runtime/full_body.png");
        WriteAsset(Encoding.UTF8.GetBytes("expr-bytes"), "runtime/expressions/r01c01.png");
        var json = PackFixture.V3Json([
            ("body", "full_body", "runtime/full_body.png", PackFixture.Sha256(Encoding.UTF8.GetBytes("body-bytes"))),
            ("expression", "r01c01", "runtime/expressions/r01c01.png", PackFixture.Sha256(Encoding.UTF8.GetBytes("expr-bytes"))),
        ]);
        return AppearanceManifestReader.Read(WriteManifest(json));
    }

    private void WriteAsset(byte[] content, string relativePath)
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
    }

    private string WriteManifest(string json)
    {
        var path = Path.Combine(_root, "manifest.json");
        File.WriteAllText(path, json);
        return path;
    }
}