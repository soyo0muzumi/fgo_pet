using FgoPet.Core.Portraits;
using FgoPet.Infrastructure.Packs;
using Xunit;

namespace FgoPet.Infrastructure.Tests.Packs;

public sealed class DialogueManifestReaderTests
{
    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", "packs", name);

    [Fact]
    public void ReadOptional_loads_plain_text_candidates_and_default_locale()
    {
        var bundle = DialogueManifestReader.ReadOptional(Fixture("dialogue-valid"));
        Assert.NotNull(bundle);
        Assert.Equal("zh-CN", bundle!.DefaultLocale);
        Assert.Equal("focus_started_01", bundle.Localizations["zh-CN"].Events["focus_started"][0].Id);
        Assert.Equal("开始一段专注吧。", bundle.Localizations["zh-CN"].Events["focus_started"][0].Text);
        Assert.Equal(80, bundle.Localizations["zh-CN"].Events["focus_started"][0].Weight);
        Assert.Equal(ExpressionSemantic.Happy, bundle.Localizations["zh-CN"].Events["focus_started"][0].Expression);
    }

    [Fact]
    public void ReadOptional_returns_null_when_dialogue_directory_is_absent()
    {
        var root = Directory.CreateTempSubdirectory("fgo-no-dialogue-").FullName;
        try
        {
            Assert.Null(DialogueManifestReader.ReadOptional(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ReadOptional_returns_null_for_an_invalid_expression_value()
    {
        var bundle = DialogueManifestReader.ReadOptional(Fixture("dialogue-invalid-expression"));
        Assert.Null(bundle);
    }

    [Fact]
    public void ReadOptional_rejects_unknown_manifest_properties()
    {
        var root = Directory.CreateTempSubdirectory("fgo-dialogue-unknown-").FullName;
        try
        {
            var dialogue = Path.Combine(root, "dialogue");
            Directory.CreateDirectory(dialogue);
            File.WriteAllText(Path.Combine(dialogue, "manifest.json"),
                """{"schema_version":1,"default_locale":"zh-CN","localizations":{"zh-CN":"zh-CN.json"},"extra_key":1}""");
            File.WriteAllText(Path.Combine(dialogue, "zh-CN.json"), """{"focus_started":[]}""");

            Assert.Null(DialogueManifestReader.ReadOptional(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ReadOptional_rejects_text_longer_than_160_scalars()
    {
        var root = Directory.CreateTempSubdirectory("fgo-dialogue-long-").FullName;
        try
        {
            var dialogue = Path.Combine(root, "dialogue");
            Directory.CreateDirectory(dialogue);
            File.WriteAllText(Path.Combine(dialogue, "manifest.json"),
                """{"schema_version":1,"default_locale":"zh-CN","localizations":{"zh-CN":"zh-CN.json"}}""");
            var longText = new string('好', 161);
            File.WriteAllText(Path.Combine(dialogue, "zh-CN.json"),
                $$"""{"focus_started":[{"id":"too_long_01","text":"{{longText}}"}]}""");

            Assert.Null(DialogueManifestReader.ReadOptional(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
