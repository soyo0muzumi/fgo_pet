using FgoPet.Core.Agents;
using FgoPet.Core.Portraits;
using FgoPet.Core.Settings;
using FgoPet.Infrastructure.Settings;
using Xunit;

namespace FgoPet.Infrastructure.Tests.Settings;

public sealed class AppSettingsSnapshotCodecTests
{
    [Fact]
    public void Safe_snapshot_roundtrips_all_non_secret_settings()
    {
        var source = AppSettings.Defaults with
        {
            Selection = new PortraitSelection("official.mash", "casual", "1.0.0"),
            Scale = 0.75,
            Topmost = false,
            AutoCollapseExpandedPanel = false,
            ModelConnection = new ModelConnectionSettings("openai", "https://api.openai.com/v1", "gpt-4o-mini"),
            MemoryEnabled = false,
            ServantPreferences = new Dictionary<string, ServantPreference>
            {
                ["mash_kyrielight"] = new(AddressMode.UserDefined, "御主"),
            },
            Theme = AppTheme.FgoLight,
            UserProfile = new UserProfile("xqj"),
            PackageSettings = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["mash_kyrielight"] = new Dictionary<string, string> { ["show_status"] = "true" },
            },
            AgentConnection = new AgentConnectionSettings(
                Enabled: true,
                SourceEnabled: new Dictionary<string, bool> { ["codex"] = true },
                ProjectAllowlist: new Dictionary<string, IReadOnlyList<AgentProjectTarget>>
                {
                    ["codex"] = new[] { new AgentProjectTarget("project-1", "Project") },
                }),
        };

        var codec = new AppSettingsSnapshotCodec();
        var json = codec.Serialize(source);
        var restored = codec.Deserialize(json);

        Assert.Equal(source.Selection, restored.Selection);
        Assert.Equal(source.Scale, restored.Scale);
        Assert.Equal(source.Topmost, restored.Topmost);
        Assert.Equal(source.AutoCollapseExpandedPanel, restored.AutoCollapseExpandedPanel);
        Assert.Equal(source.ModelConnection, restored.ModelConnection);
        Assert.Equal(source.MemoryEnabled, restored.MemoryEnabled);
        Assert.Equal(source.ServantPreferences, restored.ServantPreferences);
        Assert.Equal(source.Theme, restored.Theme);
        Assert.Equal(source.UserProfile, restored.UserProfile);
        Assert.Equal(source.PackageSettings, restored.PackageSettings);
        Assert.Equal(source.AgentConnection.Enabled, restored.AgentConnection.Enabled);
        Assert.Equal(source.AgentConnection.SourceEnabled, restored.AgentConnection.SourceEnabled);
        Assert.Equal(source.AgentConnection.ProjectAllowlist, restored.AgentConnection.ProjectAllowlist);
        Assert.Contains("\"model_connection\"", json, StringComparison.Ordinal);
        Assert.Contains("\"agent_connection\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("api_key", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_an_unsupported_snapshot_schema()
    {
        var codec = new AppSettingsSnapshotCodec();

        Assert.ThrowsAny<Exception>(() => codec.Deserialize("{\"schema_version\":99}"));
    }
}
