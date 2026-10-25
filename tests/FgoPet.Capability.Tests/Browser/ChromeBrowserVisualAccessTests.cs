using System.Text.Json;
using FgoPet.Plugin.Browser;
using Xunit;

namespace FgoPet.Capability.Tests.Browser;

public sealed class ChromeBrowserVisualAccessTests
{
    [Fact]
    public void Grant_defaults_off_preserves_setup_and_regrant_never_revives_previous_authority()
    {
        var path = Path.Combine(Path.GetTempPath(), "browser-grant-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var access = new ChromeBrowserVisualAccess(path);
            Assert.False(access.IsAvailable); Assert.False(access.TrySetEnabled(true));
            var settings = new ChromeBrowserSettings(true, Path.GetFullPath("node.exe"), Path.GetFullPath("server.js"));
            File.WriteAllText(path, JsonSerializer.Serialize(settings));
            Assert.True(access.IsAvailable); Assert.False(access.Enabled);
            Assert.True(access.TrySetEnabled(true)); var original = ChromeBrowserSettings.Load(path)!;
            Assert.True(original.VisualEnabled); Assert.Equal(settings.NodePath, original.NodePath);
            Assert.Equal(settings.ServerPath, original.ServerPath);
            Assert.True(access.TrySetEnabled(false)); Assert.False(access.Enabled);
            Assert.True(access.TrySetEnabled(true)); Assert.NotEqual(original.VisualGrantId, ChromeBrowserSettings.Load(path)!.VisualGrantId);
        }
        finally { File.Delete(path); }
    }
}
