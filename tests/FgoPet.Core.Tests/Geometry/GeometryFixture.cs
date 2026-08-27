using FgoPet.Core.Geometry;

namespace FgoPet.Core.Tests.Geometry;

internal static class GeometryFixture
{
    /// <summary>Mash casual: 303x603 body, 256x240 overlay at (13,0), panel anchor (151,360).</summary>
    public static readonly PortraitSourceGeometry MashGeometry = new(
        BodyPixelWidth: 303,
        BodyPixelHeight: 603,
        OverlayPixelX: 13,
        OverlayPixelY: 0,
        OverlayPixelWidth: 256,
        OverlayPixelHeight: 240,
        PanelAnchorX: 151,
        PanelAnchorY: 360);

    /// <summary>Matches the production alignment convention: round-to-even on `.5` boundaries.</summary>
    public static int Round(double value) => (int)Math.Round(value);
}