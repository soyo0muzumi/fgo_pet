using System.Diagnostics;
using System.IO;
using FgoPet.App.Panels;
using FgoPet.App.Portraits;
using FgoPet.Core.Geometry;
using FgoPet.Core.Packs;
using FgoPet.Core.Portraits;
using Xunit;

namespace FgoPet.Windows.Tests.Soak;

[Trait("Category", "WindowsIntegration")]
public sealed class PortraitSoakTests
{
    [Fact]
    public void Soak_cycles_expressions_eviction_and_panel_without_unbounded_state()
    {
        var mashFixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "packs", "mash-art-v3.json");
        var manifest = PackJson.DeserializeStrict<AppearanceManifestV3>(File.ReadAllText(mashFixture));
        var resolver = new ExpressionResolver();
        var samples = new List<long>();

        // 28 cycles over all eight core semantics.
        for (var cycle = 0; cycle < 28; cycle++)
        {
            foreach (var semantic in Enum.GetValues<ExpressionSemantic>())
            {
                var resolution = resolver.Resolve(semantic, manifest);
                Assert.True(manifest.HasExpressionAsset(resolution.AssetId));
            }
        }

        // Three appearances to exercise the bounded snapshot cache.
        var cache = new PortraitSnapshotCache();
        for (var index = 1; index <= 3; index++)
        {
            var selection = new PortraitSelection($"soak.pkg", "appearance", index.ToString());
            cache.Put(selection, Snapshot(index));
        }
        Assert.Equal(PortraitSnapshotCache.Capacity, cache.Count);
        Assert.Null(cache.TryGet(new PortraitSelection("soak.pkg", "appearance", "1")));

        // One thousand panel open/close transitions.
        var panel = new AttachedPanelViewModel(TimeProvider.System);
        for (var i = 0; i < 1000; i++)
        {
            panel.PortraitClick();
            panel.DialogueClick();
            panel.TodoClick();
            panel.Escape();
        }
        Assert.Equal(Core.Panels.AttachedPanelState.Collapsed, panel.State);
        Assert.Equal(0, panel.VisibleDialogueCount);
        Assert.Equal(0, panel.VisibleTodoCount);

        // Working-set samples are recorded but no fixed memory ceiling is asserted.
        samples.Add(Process.GetCurrentProcess().WorkingSet64);
        Assert.NotEmpty(samples);
    }

    private static PortraitSnapshot Snapshot(int index) => new(
        new Dictionary<string, System.Windows.Media.Imaging.BitmapSource>(),
        "body",
        "expr",
        new Dictionary<string, byte[]>(),
        new PortraitSourceGeometry(303, 603, 13, 0, 256, 240, 151, 360));
}