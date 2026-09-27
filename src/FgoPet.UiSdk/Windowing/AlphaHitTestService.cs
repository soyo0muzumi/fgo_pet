using System.Windows;
using FgoPet.Core.Geometry;
using FgoPet.Core.Windowing;
using Point = System.Windows.Point;

namespace FgoPet.App.Windowing;

/// <summary>
/// Source-coordinate Alpha hit testing against the body and the currently displayed
/// expression masks. Pure integer math over precomputed masks: no per-query allocation.
/// </summary>
/// <remarks>
/// Takes <see cref="IAlphaMaskSource"/> rather than the character module's concrete
/// <c>PortraitSnapshot</c>, so the windowing layer stays independent of any business module
/// (R8 in findings/35-split-path-blockers.md).
/// </remarks>
public static class AlphaHitTestService
{
    public const byte MinVisibleAlpha = 1;

    public static bool IsHit(Point logicalPoint, IAlphaMaskSource source, string expressionAssetId, PortraitGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(geometry);

        var sourceGeometry = source.SourceGeometry;
        var scale = geometry.BodyLogicalRect.Width / sourceGeometry.BodyPixelWidth;
        if (scale <= 0)
        {
            return false;
        }

        var hit = false;
        var bodyMask = source.AlphaMasks[source.BodyId];
        var bodyX = (int)Math.Floor((logicalPoint.X - geometry.BodyLogicalRect.X) / scale);
        var bodyY = (int)Math.Floor((logicalPoint.Y - geometry.BodyLogicalRect.Y) / scale);
        if (bodyX >= 0 && bodyY >= 0 && bodyX < sourceGeometry.BodyPixelWidth && bodyY < sourceGeometry.BodyPixelHeight)
        {
            hit |= bodyMask[(bodyY * sourceGeometry.BodyPixelWidth) + bodyX] >= MinVisibleAlpha;
        }

        var overlayMask = source.AlphaMasks[expressionAssetId];
        var overlay = geometry.OverlayLogicalRect;
        var overlayX = (int)Math.Floor((logicalPoint.X - overlay.X) / scale);
        var overlayY = (int)Math.Floor((logicalPoint.Y - overlay.Y) / scale);
        if (overlayX >= 0 && overlayY >= 0
            && overlayX < sourceGeometry.OverlayPixelWidth && overlayY < sourceGeometry.OverlayPixelHeight)
        {
            hit |= overlayMask[(overlayY * sourceGeometry.OverlayPixelWidth) + overlayX] >= MinVisibleAlpha;
        }

        return hit;
    }
}
