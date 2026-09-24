using System.Text.Json;
using FgoPet.Core.Settings;
using FgoPet.Core.Speech;
using Xunit;

namespace FgoPet.SettingsHost.Tests;

public sealed class SettingsDocumentV2CodecTests
{
    [Theory]
    [InlineData("context_window_override", "-1")]
    [InlineData("context_window_override", "2147483648")]
    [InlineData("max_output_tokens", "0")]
    public void Invalid_context_configuration_is_rejected(string name, string value)
    {
        var json = File.ReadAllText(Fixture("settings-v2-complete.json"));
        var root = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        root["model_connection"]![name] = System.Text.Json.Nodes.JsonNode.Parse(value);
        Assert.Throws<JsonException>(() => new SettingsDocumentV2Codec().Deserialize(root.ToJsonString()));
    }

    [Fact]
    public void Context_and_output_limits_round_trip_without_changing_legacy_defaults()
    {
        var codec = new SettingsDocumentV2Codec();
        var old = codec.Deserialize(File.ReadAllText(Fixture("settings-v2-complete.json")));
        Assert.Null(old.Dialogue.ModelConnection!.ContextWindowOverride);
        Assert.Equal(2048, old.Dialogue.ModelConnection.MaxOutputTokens);
        var updated = old with { Dialogue = old.Dialogue with {
            ModelConnection = new ModelConnectionSettings("test", "https://example.test", "m",
                contextWindowOverride: 32768, maxOutputTokens: 4096) } };
        var json = codec.Serialize(updated);
        Assert.Contains("\"context_window_override\":32768", json);
        var restored = codec.Deserialize(codec.SerializeForBackup(updated)).Dialogue.ModelConnection!;
        Assert.Equal(32768, restored.ContextWindowOverride);
        Assert.Equal(4096, restored.MaxOutputTokens);
    }

    [Fact]
    public void Tools_support_downgrade_survives_save_and_backup_without_changing_legacy_default()
    {
        var codec = new SettingsDocumentV2Codec();
        var legacy = codec.Deserialize(File.ReadAllText(Fixture("settings-v2-complete.json")));
        Assert.True(legacy.Dialogue.ModelConnection!.ToolsSupported);
        var disabled = legacy with { Dialogue = legacy.Dialogue with { ModelConnection = legacy.Dialogue.ModelConnection with { ToolsSupported = false } } };

        Assert.False(codec.Deserialize(codec.Serialize(disabled)).Dialogue.ModelConnection!.ToolsSupported);
        Assert.False(codec.Deserialize(codec.SerializeForBackup(disabled)).Dialogue.ModelConnection!.ToolsSupported);
    }

    [Fact]
    public void Deserialize_complete_v2_fixture_maps_every_field_from_hand_derived_expectations()
    {
        var snapshot = new SettingsDocumentV2Codec().Deserialize(
            File.ReadAllText(Fixture("settings-v2-complete.json")));

        Assert.NotNull(snapshot.Character.Selection);
        Assert.Equal("fixture.package", snapshot.Character.Selection.PackageId);
        Assert.Equal("default", snapshot.Character.Selection.AppearanceId);
        Assert.Equal("1.0.0", snapshot.Character.Selection.PackageVersion);
        Assert.Equal(0.75, snapshot.Character.Scale);
        Assert.False(snapshot.Character.Topmost);
        Assert.False(snapshot.Character.AutoCollapseExpandedPanel);
        var preference = Assert.Single(snapshot.Character.ServantPreferences);
        Assert.Equal("mash", preference.Key);
        Assert.Equal(AddressMode.UserDefined, preference.Value.AddressMode);
        Assert.Equal("Master", preference.Value.AddressText);
        Assert.False(snapshot.Character.ServantPreferences.ContainsKey("MASH"));
        Assert.NotNull(snapshot.Character.UserProfile);
        Assert.Equal("Fixture User", snapshot.Character.UserProfile.DisplayName);
        var package = Assert.Single(snapshot.Character.PackageSettings);
        Assert.Equal("mash", package.Key);
        var packageSetting = Assert.Single(package.Value);
        Assert.Equal("costume", packageSetting.Key);
        Assert.Equal("casual", packageSetting.Value);
        Assert.False(snapshot.Character.PackageSettings.ContainsKey("MASH"));

        Assert.NotNull(snapshot.Dialogue.ModelConnection);
        Assert.Equal("fixture", snapshot.Dialogue.ModelConnection.ProviderId);
        Assert.Equal("http://127.0.0.1:12345", snapshot.Dialogue.ModelConnection.BaseUrl);
        Assert.Equal("fixture-model", snapshot.Dialogue.ModelConnection.ModelId);
        Assert.False(snapshot.Dialogue.ShowReasoning);
        Assert.False(snapshot.Memory.Enabled);

        var agent = snapshot.WorkExecution.AgentConnection;
        Assert.True(agent.Enabled);
        var source = Assert.Single(agent.SourceEnabled);
        Assert.Equal("codex", source.Key);
        Assert.True(source.Value);
        Assert.False(agent.SourceEnabled.ContainsKey("CODEX"));
        var allowedProjects = Assert.Single(agent.ProjectAllowlist);
        Assert.Equal("codex", allowedProjects.Key);
        var target = Assert.Single(allowedProjects.Value);
        Assert.Equal("fixture-target", target.TargetId);
        Assert.Equal("Fixture", target.DisplayName);

        var speech = snapshot.Speech.Connection;
        Assert.True(speech.Enabled);
        Assert.Equal(SpeechProviderKind.IndexTts, speech.Provider);
        Assert.Equal("https://api.openai.com/v1", speech.OpenAiBaseUrl);
        Assert.Equal("gpt-4o-mini-tts", speech.OpenAiModel);
        Assert.Equal("alloy", speech.OpenAiVoice);
        Assert.Equal("fgo-pet/speech/openai", speech.OpenAiCredentialTarget);
        Assert.Equal("http://127.0.0.1:9880", speech.GptSoVitsBaseUrl);
        Assert.Equal("", speech.GptSoVitsReferenceAudioPath);
        Assert.Equal("", speech.GptSoVitsPromptText);
        Assert.Equal("zh", speech.GptSoVitsLanguage);
        Assert.Equal("zh", speech.GptSoVitsPromptLanguage);
        Assert.True(speech.AutoReadEnabled);
        Assert.Equal(240, speech.AutoReadLimit);
        Assert.Equal(1.1, speech.Rate);
        Assert.Equal(0.8, speech.Volume);
        Assert.Equal("http://127.0.0.1:7860", speech.IndexTtsBaseUrl);
        Assert.Equal("voice-1", speech.IndexTtsVoiceId);
        Assert.Empty(speech.IndexTtsVoices);
        Assert.True(speech.DoNotDisturb);

        Assert.Equal(AppTheme.ModernGray, snapshot.Theme.Theme);
    }

    [Theory]
    [InlineData("settings-v1-minimal.json")]
    [InlineData("settings-v2-complete.json")]
    public void Deserialize_and_round_trip_supported_documents(string fixture)
    {
        var json = File.ReadAllText(Fixture(fixture));
        var codec = new SettingsDocumentV2Codec();
        var first = codec.Deserialize(json);
        var second = codec.Deserialize(codec.Serialize(first));
        AssertEquivalent(first, second);
    }

    [Fact]
    public void Deserialize_minimal_v1_fixture_applies_literal_compatibility_defaults()
    {
        var snapshot = new SettingsDocumentV2Codec().Deserialize(
            File.ReadAllText(Fixture("settings-v1-minimal.json")));

        Assert.Null(snapshot.Character.Selection);
        Assert.Equal(0.5, snapshot.Character.Scale);
        Assert.True(snapshot.Character.Topmost);
        Assert.True(snapshot.Character.AutoCollapseExpandedPanel);
        Assert.Empty(snapshot.Character.ServantPreferences);
        Assert.Null(snapshot.Character.UserProfile);
        Assert.Empty(snapshot.Character.PackageSettings);

        Assert.Null(snapshot.Dialogue.ModelConnection);
        Assert.True(snapshot.Dialogue.ShowReasoning);
        Assert.True(snapshot.Memory.Enabled);

        Assert.False(snapshot.WorkExecution.AgentConnection.Enabled);
        Assert.Empty(snapshot.WorkExecution.AgentConnection.SourceEnabled);
        Assert.Empty(snapshot.WorkExecution.AgentConnection.ProjectAllowlist);

        var speech = snapshot.Speech.Connection;
        Assert.False(speech.Enabled);
        Assert.Equal(SpeechProviderKind.OpenAiCompatible, speech.Provider);
        Assert.Equal("https://api.openai.com/v1", speech.OpenAiBaseUrl);
        Assert.Equal("gpt-4o-mini-tts", speech.OpenAiModel);
        Assert.Equal("alloy", speech.OpenAiVoice);
        Assert.Equal("fgo-pet/speech/openai", speech.OpenAiCredentialTarget);
        Assert.Equal("http://127.0.0.1:9880", speech.GptSoVitsBaseUrl);
        Assert.Equal(string.Empty, speech.GptSoVitsReferenceAudioPath);
        Assert.Equal(string.Empty, speech.GptSoVitsPromptText);
        Assert.Equal("zh", speech.GptSoVitsLanguage);
        Assert.Equal("zh", speech.GptSoVitsPromptLanguage);
        Assert.Equal("http://127.0.0.1:7860", speech.IndexTtsBaseUrl);
        Assert.Equal(string.Empty, speech.IndexTtsVoiceId);
        Assert.Empty(speech.IndexTtsVoices);
        Assert.False(speech.AutoReadEnabled);
        Assert.False(speech.DoNotDisturb);
        Assert.Equal(300, speech.AutoReadLimit);
        Assert.Equal(1.0, speech.Rate);
        Assert.Equal(1.0, speech.Volume);

        Assert.Equal(AppTheme.FgoLight, snapshot.Theme.Theme);
    }

    [Fact]
    public void Serialize_v1_document_writes_schema_v2()
    {
        var codec = new SettingsDocumentV2Codec();
        var snapshot = codec.Deserialize(File.ReadAllText(Fixture("settings-v1-minimal.json")));

        using var document = JsonDocument.Parse(codec.Serialize(snapshot));

        Assert.Equal(2, document.RootElement.GetProperty("schema_version").GetInt32());
    }

    [Fact]
    public void Rejects_unsupported_schema()
    {
        Assert.Throws<JsonException>(() =>
            new SettingsDocumentV2Codec().Deserialize("{\"schema_version\":3}"));
    }

    [Theory]
    [InlineData("{\"schema_version\":2,\"speech_connection\":{\"enabled\":true,\"provider\":\"unknown\"}}")]
    [InlineData("{\"schema_version\":2,\"agent_connection\":{\"enabled\":true,\"project_allowlist\":{\"codex\":[{\"target_id\":\"\",\"display_name\":\"Fixture\"}]}}}")]
    public void Rejects_invalid_owned_section_values(string json)
    {
        Assert.Throws<JsonException>(() => new SettingsDocumentV2Codec().Deserialize(json));
    }

    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static void AssertEquivalent(SettingsDocumentSnapshot expected, SettingsDocumentSnapshot actual)
    {
        Assert.Equal(expected.Character.Selection, actual.Character.Selection);
        Assert.Equal(expected.Character.Scale, actual.Character.Scale);
        Assert.Equal(expected.Character.Topmost, actual.Character.Topmost);
        Assert.Equal(expected.Character.AutoCollapseExpandedPanel, actual.Character.AutoCollapseExpandedPanel);
        Assert.Equal(expected.Character.UserProfile, actual.Character.UserProfile);
        Assert.Equal(expected.Character.ServantPreferences.Keys, actual.Character.ServantPreferences.Keys);
        foreach (var preference in expected.Character.ServantPreferences)
        {
            Assert.Equal(preference.Value, actual.Character.ServantPreferences[preference.Key]);
        }

        Assert.Equal(expected.Character.PackageSettings.Keys, actual.Character.PackageSettings.Keys);
        foreach (var package in expected.Character.PackageSettings)
        {
            Assert.Equal(package.Value.Keys, actual.Character.PackageSettings[package.Key].Keys);
            foreach (var setting in package.Value)
            {
                Assert.Equal(setting.Value, actual.Character.PackageSettings[package.Key][setting.Key]);
            }
        }

        Assert.Equal(expected.Dialogue, actual.Dialogue);
        Assert.Equal(expected.Memory, actual.Memory);
        Assert.Equal(expected.Theme, actual.Theme);

        var expectedAgent = expected.WorkExecution.AgentConnection;
        var actualAgent = actual.WorkExecution.AgentConnection;
        Assert.Equal(expectedAgent.Enabled, actualAgent.Enabled);
        Assert.Equal(expectedAgent.SourceEnabled, actualAgent.SourceEnabled);
        Assert.Equal(expectedAgent.ProjectAllowlist.Keys, actualAgent.ProjectAllowlist.Keys);
        foreach (var source in expectedAgent.ProjectAllowlist)
        {
            Assert.Equal(source.Value, actualAgent.ProjectAllowlist[source.Key]);
        }

        var expectedSpeech = expected.Speech.Connection;
        var actualSpeech = actual.Speech.Connection;
        Assert.Equal(expectedSpeech.Enabled, actualSpeech.Enabled);
        Assert.Equal(expectedSpeech.Provider, actualSpeech.Provider);
        Assert.Equal(expectedSpeech.OpenAiBaseUrl, actualSpeech.OpenAiBaseUrl);
        Assert.Equal(expectedSpeech.OpenAiModel, actualSpeech.OpenAiModel);
        Assert.Equal(expectedSpeech.OpenAiVoice, actualSpeech.OpenAiVoice);
        Assert.Equal(expectedSpeech.OpenAiCredentialTarget, actualSpeech.OpenAiCredentialTarget);
        Assert.Equal(expectedSpeech.GptSoVitsBaseUrl, actualSpeech.GptSoVitsBaseUrl);
        Assert.Equal(expectedSpeech.GptSoVitsReferenceAudioPath, actualSpeech.GptSoVitsReferenceAudioPath);
        Assert.Equal(expectedSpeech.GptSoVitsPromptText, actualSpeech.GptSoVitsPromptText);
        Assert.Equal(expectedSpeech.GptSoVitsLanguage, actualSpeech.GptSoVitsLanguage);
        Assert.Equal(expectedSpeech.GptSoVitsPromptLanguage, actualSpeech.GptSoVitsPromptLanguage);
        Assert.Equal(expectedSpeech.IndexTtsBaseUrl, actualSpeech.IndexTtsBaseUrl);
        Assert.Equal(expectedSpeech.IndexTtsVoiceId, actualSpeech.IndexTtsVoiceId);
        Assert.Equal(expectedSpeech.IndexTtsVoices, actualSpeech.IndexTtsVoices);
        Assert.Equal(expectedSpeech.AutoReadEnabled, actualSpeech.AutoReadEnabled);
        Assert.Equal(expectedSpeech.DoNotDisturb, actualSpeech.DoNotDisturb);
        Assert.Equal(expectedSpeech.AutoReadLimit, actualSpeech.AutoReadLimit);
        Assert.Equal(expectedSpeech.Rate, actualSpeech.Rate);
        Assert.Equal(expectedSpeech.Volume, actualSpeech.Volume);
    }
}
