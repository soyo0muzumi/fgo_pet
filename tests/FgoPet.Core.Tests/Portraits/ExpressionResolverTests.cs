using FgoPet.Core.Packs;
using FgoPet.Core.Portraits;
using FgoPet.Core.Tests.Packs;
using Xunit;

namespace FgoPet.Core.Tests.Portraits;

public sealed class ExpressionResolverTests
{
    private readonly IExpressionResolver _resolver = new ExpressionResolver();

    [Fact]
    public void Resolve_follows_mapping_then_fallback_to_neutral()
    {
        var manifest = AppearanceFixture.Appearance(
            mapping: new Dictionary<string, string>
            {
                ["sad"] = "missing",
                ["neutral"] = "face01",
            },
            fallback: new Dictionary<string, string>
            {
                ["sad"] = "neutral",
            });

        Assert.Equal(
            new ExpressionResolution(ExpressionSemantic.Sad, "face01", UsedFallback: true),
            _resolver.Resolve(ExpressionSemantic.Sad, manifest));
    }

    [Fact]
    public void Resolve_uses_direct_mapping_without_fallback()
    {
        var manifest = AppearanceFixture.Appearance(
            mapping: new Dictionary<string, string>
            {
                ["sad"] = "face01",
                ["neutral"] = "face01",
            });

        Assert.Equal(
            new ExpressionResolution(ExpressionSemantic.Sad, "face01", UsedFallback: false),
            _resolver.Resolve(ExpressionSemantic.Sad, manifest));
    }

    [Fact]
    public void Resolve_fallback_cycle_throws_ExpressionMappingInvalid()
    {
        var manifest = AppearanceFixture.Appearance(
            mapping: new Dictionary<string, string>
            {
                ["sad"] = "missing",
                ["angry"] = "missing",
            },
            fallback: new Dictionary<string, string>
            {
                ["sad"] = "angry",
                ["angry"] = "sad",
            });

        var failure = Assert.Throws<PackFailureException>(() =>
            _resolver.Resolve(ExpressionSemantic.Sad, manifest));
        Assert.Equal(PackErrorCode.ExpressionMappingInvalid, failure.Failure.Code);
    }

    [Fact]
    public void Resolve_neutral_terminates_a_dead_end()
    {
        var manifest = AppearanceFixture.Appearance(
            mapping: new Dictionary<string, string>
            {
                ["angry"] = "missing",
                ["neutral"] = "face01",
            });

        Assert.Equal(
            new ExpressionResolution(ExpressionSemantic.Angry, "face01", UsedFallback: true),
            _resolver.Resolve(ExpressionSemantic.Angry, manifest));
    }

    [Fact]
    public void Resolve_unresolvable_semantic_throws()
    {
        var manifest = AppearanceFixture.Appearance(
            mapping: new Dictionary<string, string>
            {
                ["angry"] = "missing",
            },
            expressionIds: new[] { "face01", "face02", "face03", "face04", "face05" });

        var failure = Assert.Throws<PackFailureException>(() =>
            _resolver.Resolve(ExpressionSemantic.Angry, manifest));
        Assert.Equal(PackErrorCode.ExpressionMappingInvalid, failure.Failure.Code);
    }

    [Fact]
    public void Resolve_every_core_semantic_from_the_mash_fixture()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "packs", "mash-art-v3.json"));
        var manifest = PackJson.DeserializeStrict<AppearanceManifestV3>(json);

        foreach (var semantic in Enum.GetValues<ExpressionSemantic>())
        {
            var result = _resolver.Resolve(semantic, manifest);
            Assert.NotEqual(string.Empty, result.AssetId);
            Assert.True(manifest.HasExpressionAsset(result.AssetId),
                $"{semantic} resolved to an undeclared asset '{result.AssetId}'.");
        }
    }
}