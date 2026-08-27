using System.IO;
using System.Windows;
using FgoPet.App.Portraits;
using FgoPet.App.Tests.Portraits;
using FgoPet.App.Windowing;
using FgoPet.Core.Geometry;
using FgoPet.Infrastructure.Packs;
using Xunit;

namespace FgoPet.App.Tests.Windowing;

public sealed class AlphaHitTestServiceTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "fgo-pet-hittest-" + Guid.NewGuid().ToString("N"));

    public AlphaHitTestServiceTests() => Directory.CreateDirectory(_temp);

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
    public void IsHit_is_true_on_an_opaque_body_pixel()
    {
        var snapshot = Snapshot(FirstTransparentExceptCorner);
        var geometry = Geometry(snapshot);

        Assert.True(AlphaHitTestService.IsHit(new Point(110, 250), snapshot, "r01c01", geometry));
    }

    [Fact]
    public void IsHit_is_false_for_a_transparent_region_away_from_the_overlay()
    {
        var snapshot = Snapshot(FirstTransparentExceptCorner);
        var geometry = Geometry(snapshot);

        Assert.False(AlphaHitTestService.IsHit(new Point(25, 250), snapshot, "r01c01", geometry));
    }

    [Fact]
    public void IsHit_uses_the_overlay_where_the_body_is_transparent()
    {
        var snapshot = Snapshot(FirstOpaque);
        var geometry = Geometry(snapshot);

        Assert.True(AlphaHitTestService.IsHit(new Point(40, 60), snapshot, "r01c01", geometry));
    }

    [Fact]
    public void IsHit_is_false_outside_the_character()
    {
        var snapshot = Snapshot(FirstTransparentExceptCorner);
        var geometry = Geometry(snapshot);

        Assert.False(AlphaHitTestService.IsHit(new Point(10, 400), snapshot, "r01c01", geometry));
    }

    [Fact]
    public void IsHit_consults_the_current_expression_mask()
    {
        var snapshot = Snapshot(FirstTransparentExceptCorner, secondOpaque: true);
        var geometry = Geometry(snapshot);

        // r01c01 is transparent in the overlay middle; r01c02 is opaque there.
        Assert.False(AlphaHitTestService.IsHit(new Point(40, 60), snapshot, "r01c01", geometry));
        Assert.True(AlphaHitTestService.IsHit(new Point(40, 60), snapshot, "r01c02", geometry));
    }

    private static PortraitGeometry Geometry(PortraitSnapshot snapshot) =>
        PortraitLayout.Calculate(snapshot.SourceGeometry, 0.50, new Dpi2(1.0, 1.0));

    private static byte FirstTransparentExceptCorner(int x, int y) => x < 2 && y < 2 ? (byte)255 : (byte)0;

    private static byte FirstOpaque(int x, int y) => 255;

    private PortraitSnapshot Snapshot(Func<int, int, byte> firstAlphaAt, bool secondOpaque = false)
    {
        // Body: left half transparent, right half opaque.
        var body = AppearanceBundle.CreatePng(303, 603, alpha: 255, alphaAt: (x, _) => x < 151 ? (byte)0 : (byte)255);
        var first = AppearanceBundle.CreatePng(256, 240, alpha: 0, alphaAt: firstAlphaAt);
        var bundle = AppearanceBundle.Write(
            _temp,
            body,
            first,
            expressionPng2: secondOpaque ? AppearanceBundle.CreatePng(256, 240, alpha: 200) : null);

        var manifest = AppearanceManifestReader.Read(bundle.ManifestPath);
        var validated = AppearanceValidator.Validate(manifest, bundle.Root).Value!;
        return BitmapAssetLoader.LoadValidated(validated);
    }
}