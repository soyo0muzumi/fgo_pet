using System.Text.Json.Nodes;
using FgoPet.Core.Packs;
using Xunit;

namespace FgoPet.Infrastructure.Tests.Packs;

public sealed class PackManifestTests
{
    [Fact]
    public void Package_settings_accept_the_declared_toggle_choice_and_text_definitions()
    {
        var manifest = PackJson.DeserializeStrict<PackManifestV1>(ManifestJson(
            Setting("show_status", "显示状态", "toggle", "true"),
            Setting("voice", "语音", "choice", "jp", ["jp", "cn"]),
            Setting("greeting", "问候", "text", "早上好")));

        Assert.Equal(3, manifest.Settings.Count);
        Assert.Equal(PackSettingType.Toggle, manifest.Settings[0].Type);
        Assert.Equal(["jp", "cn"], manifest.Settings[1].Options);
        Assert.Equal("早上好", manifest.Settings[2].Default);
    }

    [Fact]
    public void Package_settings_accept_keys_that_begin_with_a_digit()
    {
        var manifest = PackJson.DeserializeStrict<PackManifestV1>(ManifestJson(
            Setting("1status", "显示状态", "toggle", "true")));

        Assert.Equal("1status", Assert.Single(manifest.Settings).Key);
    }

    [Fact]
    public void Package_settings_reject_more_than_32_definitions()
    {
        var definitions = Enumerable.Range(0, 33)
            .Select(index => Setting($"setting_{index}", "标签", "toggle", "false"))
            .ToArray();

        AssertManifestInvalid(ManifestJson(definitions));
    }

    [Fact]
    public void Package_settings_reject_a_null_definition_array_as_malformed()
    {
        var json = ManifestJson().Replace("\"settings\":[]", "\"settings\":null", StringComparison.Ordinal);

        AssertManifestInvalid(json);
    }

    [Theory]
    [InlineData("Leading key", "标签", "toggle", "true")]
    [InlineData("valid_key", "", "toggle", "true")]
    [InlineData("valid_key", "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx", "toggle", "true")]
    [InlineData("valid_key", "标签", "range", "5")]
    public void Package_settings_reject_invalid_key_label_or_type(string key, string label, string type, string defaultValue)
    {
        AssertManifestInvalid(ManifestJson(Setting(key, label, type, defaultValue)));
    }

    [Fact]
    public void Package_settings_reject_invalid_type_specific_options_and_defaults()
    {
        AssertManifestInvalid(ManifestJson(Setting("toggle", "切换", "toggle", "yes")));
        AssertManifestInvalid(ManifestJson(Setting("toggle", "切换", "toggle", "true", ["true", "false"])));
        AssertManifestInvalid(ManifestJson(Setting("choice", "选择", "choice", "a", ["a"])));
        AssertManifestInvalid(ManifestJson(Setting("choice", "选择", "choice", "b", ["a", "a"])));
        AssertManifestInvalid(ManifestJson(Setting("choice", "选择", "choice", "missing", ["a", "b"])));
        AssertManifestInvalid(ManifestJson(Setting("text", "文本", "text", new string('x', 257))));
        AssertManifestInvalid(ManifestJson(Setting("text", "文本", "text", "okay", ["unexpected", "option"])));
    }

    [Fact]
    public void Package_settings_reject_duplicate_keys_and_unknown_definition_properties()
    {
        AssertManifestInvalid(ManifestJson(
            Setting("shared", "一", "toggle", "true"),
            Setting("shared", "二", "toggle", "false")));

        var unknown = Setting("known", "标签", "toggle", "true");
        unknown["unexpected"] = true;
        AssertManifestInvalid(ManifestJson(unknown));
    }

    private static void AssertManifestInvalid(string json)
    {
        var failure = Assert.Throws<PackFailureException>(() => PackJson.DeserializeStrict<PackManifestV1>(json));
        Assert.Equal(PackErrorCode.ManifestMalformed, failure.Failure.Code);
    }

    private static string ManifestJson(params JsonObject[] settings)
    {
        var manifest = new JsonObject
        {
            ["schema_version"] = 1,
            ["package_id"] = "official.mash",
            ["package_version"] = "1.0.0",
            ["servant_id"] = "mash_kyrielight",
            ["preview_path"] = "previews/library.png",
            ["appearances"] = new JsonArray
            {
                new JsonObject
                {
                    ["appearance_id"] = "casual",
                    ["manifest_path"] = "appearances/casual/manifest.json",
                },
            },
            ["settings"] = new JsonArray(settings),
        };
        return manifest.ToJsonString();
    }

    private static JsonObject Setting(string key, string label, string type, string defaultValue, string[]? options = null)
    {
        var definition = new JsonObject
        {
            ["key"] = key,
            ["label"] = label,
            ["type"] = type,
            ["default"] = defaultValue,
        };
        if (options is not null)
        {
            definition["options"] = new JsonArray(options.Select(value => JsonValue.Create(value)).ToArray());
        }

        return definition;
    }
}
