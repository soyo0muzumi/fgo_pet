using FgoPet.Core.Settings;
using FgoPet.Infrastructure.Settings;
using Xunit;

namespace FgoPet.Infrastructure.Tests.Settings;

public sealed class LegacySettingsCompatibilityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"fgo-settings-{Guid.NewGuid():N}");

    public LegacySettingsCompatibilityTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Theory]
    [InlineData("settings-v1-minimal.json")]
    [InlineData("settings-v2-complete.json")]
    public void Live_store_and_backup_codec_decode_the_same_supported_document(string fixture)
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture));
        File.WriteAllText(Path.Combine(_root, "settings.json"), json);

        var live = new JsonAppSettingsStore(_root).Load();
        var backup = new AppSettingsSnapshotCodec().Deserialize(json);

        AssertEquivalent(live, backup);
    }

    [Fact]
    public void Backup_codec_round_trip_preserves_every_schema_v2_value()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "settings-v2-complete.json"));
        var codec = new AppSettingsSnapshotCodec();

        AssertEquivalent(codec.Deserialize(json), codec.Deserialize(codec.Serialize(codec.Deserialize(json))));
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }

    private static void AssertEquivalent(AppSettings expected, AppSettings actual)
    {
        Assert.Equal(expected.Selection, actual.Selection);
        Assert.Equal(expected.Scale, actual.Scale);
        Assert.Equal(expected.Topmost, actual.Topmost);
        Assert.Equal(expected.AutoCollapseExpandedPanel, actual.AutoCollapseExpandedPanel);
        Assert.Equal(expected.ModelConnection, actual.ModelConnection);
        Assert.Equal(expected.MemoryEnabled, actual.MemoryEnabled);
        Assert.Equal(expected.ShowReasoning, actual.ShowReasoning);
        Assert.Equal(expected.ServantPreferences, actual.ServantPreferences);
        Assert.Equal(expected.Theme, actual.Theme);
        Assert.Equal(expected.UserProfile, actual.UserProfile);

        Assert.Equal(expected.PackageSettings.Keys, actual.PackageSettings.Keys);
        foreach (var package in expected.PackageSettings)
        {
            Assert.Equal(package.Value, actual.PackageSettings[package.Key]);
        }

        Assert.Equal(expected.AgentConnection.Enabled, actual.AgentConnection.Enabled);
        Assert.Equal(expected.AgentConnection.SourceEnabled, actual.AgentConnection.SourceEnabled);
        Assert.Equal(expected.AgentConnection.ProjectAllowlist.Keys, actual.AgentConnection.ProjectAllowlist.Keys);
        foreach (var source in expected.AgentConnection.ProjectAllowlist)
        {
            Assert.Equal(source.Value, actual.AgentConnection.ProjectAllowlist[source.Key]);
        }

        Assert.Equal(expected.SpeechConnection.Enabled, actual.SpeechConnection.Enabled);
        Assert.Equal(expected.SpeechConnection.Provider, actual.SpeechConnection.Provider);
        Assert.Equal(expected.SpeechConnection.OpenAiBaseUrl, actual.SpeechConnection.OpenAiBaseUrl);
        Assert.Equal(expected.SpeechConnection.OpenAiModel, actual.SpeechConnection.OpenAiModel);
        Assert.Equal(expected.SpeechConnection.OpenAiVoice, actual.SpeechConnection.OpenAiVoice);
        Assert.Equal(expected.SpeechConnection.OpenAiCredentialTarget, actual.SpeechConnection.OpenAiCredentialTarget);
        Assert.Equal(expected.SpeechConnection.GptSoVitsBaseUrl, actual.SpeechConnection.GptSoVitsBaseUrl);
        Assert.Equal(expected.SpeechConnection.GptSoVitsReferenceAudioPath, actual.SpeechConnection.GptSoVitsReferenceAudioPath);
        Assert.Equal(expected.SpeechConnection.GptSoVitsPromptText, actual.SpeechConnection.GptSoVitsPromptText);
        Assert.Equal(expected.SpeechConnection.GptSoVitsLanguage, actual.SpeechConnection.GptSoVitsLanguage);
        Assert.Equal(expected.SpeechConnection.GptSoVitsPromptLanguage, actual.SpeechConnection.GptSoVitsPromptLanguage);
        Assert.Equal(expected.SpeechConnection.AutoReadEnabled, actual.SpeechConnection.AutoReadEnabled);
        Assert.Equal(expected.SpeechConnection.AutoReadLimit, actual.SpeechConnection.AutoReadLimit);
        Assert.Equal(expected.SpeechConnection.Rate, actual.SpeechConnection.Rate);
        Assert.Equal(expected.SpeechConnection.Volume, actual.SpeechConnection.Volume);
        Assert.Equal(expected.SpeechConnection.IndexTtsBaseUrl, actual.SpeechConnection.IndexTtsBaseUrl);
        Assert.Equal(expected.SpeechConnection.IndexTtsVoiceId, actual.SpeechConnection.IndexTtsVoiceId);
        Assert.Equal(expected.SpeechConnection.IndexTtsVoices, actual.SpeechConnection.IndexTtsVoices);
        Assert.Equal(expected.SpeechConnection.DoNotDisturb, actual.SpeechConnection.DoNotDisturb);
    }
}
