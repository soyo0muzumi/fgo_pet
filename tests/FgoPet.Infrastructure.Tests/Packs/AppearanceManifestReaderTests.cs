using System.Text;
using FgoPet.Core.Packs;
using FgoPet.Infrastructure.Packs;
using Xunit;

namespace FgoPet.Infrastructure.Tests.Packs;

public sealed class AppearanceManifestReaderTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "fgo-pet-reader-" + Guid.NewGuid().ToString("N"));

    public AppearanceManifestReaderTests() => Directory.CreateDirectory(_temp);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
            // best effort cleanup
        }
    }

    [Fact]
    public void Read_requires_an_absolute_path()
    {
        var failure = Assert.Throws<PackFailureException>(() =>
            AppearanceManifestReader.Read("relative/manifest.json"));
        Assert.Equal(PackErrorCode.ManifestMalformed, failure.Failure.Code);
    }

    [Fact]
    public void Read_fails_when_the_manifest_is_missing()
    {
        var failure = Assert.Throws<PackFailureException>(() =>
            AppearanceManifestReader.Read(Path.Combine(_temp, "missing.json")));
        Assert.Equal(PackErrorCode.AssetMissing, failure.Failure.Code);
    }

    [Fact]
    public void Read_parses_a_valid_v3_manifest()
    {
        var content = PackFixture.V3Json([
            ("body", "full_body", "runtime/full_body.png", PackFixture.Sha256(Encoding.UTF8.GetBytes("body"))),
            ("expression", "r01c01", "runtime/expressions/r01c01.png", PackFixture.Sha256(Encoding.UTF8.GetBytes("expr"))),
        ]);
        var path = Path.Combine(_temp, "manifest.json");
        File.WriteAllText(path, content);

        var manifest = AppearanceManifestReader.Read(path);
        Assert.Equal(3, manifest.SchemaVersion);
        Assert.Equal(2, manifest.Assets.Count);
        Assert.Equal("full_body", manifest.Composition.BodyId);
    }

    [Fact]
    public void Read_rejects_unknown_json_properties()
    {
        var content = PackFixture.V3Json([
            ("body", "full_body", "runtime/full_body.png", PackFixture.Sha256(Encoding.UTF8.GetBytes("body"))),
            ("expression", "r01c01", "runtime/expressions/r01c01.png", PackFixture.Sha256(Encoding.UTF8.GetBytes("expr"))),
        ]).Replace("\"appearance_id\"", "\"unknown_field\": 1, \"appearance_id\"", StringComparison.Ordinal);
        var path = Path.Combine(_temp, "manifest.json");
        File.WriteAllText(path, content);

        var failure = Assert.Throws<PackFailureException>(() => AppearanceManifestReader.Read(path));
        Assert.Equal(PackErrorCode.ManifestMalformed, failure.Failure.Code);
    }
}