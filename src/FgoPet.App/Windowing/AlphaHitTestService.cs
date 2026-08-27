using System.Windows;
using FgoPet.App.Portraits;
using FgoPet.Core.Geometry;
using Point = System.Windows.Point;

namespace FgoPet.App.Windowing;

/// <summary>
/// Source-coordinate Alpha hit testing against the body and the currently displayed
/// expression masks. Pure integer math over precomputed masks: no per-query allocation.
/// </summary>
public static class AlphaHitTestService
{
    public const byte MinVisibleAlpha = 1;

    public static bool IsHit(Point logicalPoint, PortraitSnapshot snapshot, string expressionAssetId, PortraitGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(geometry);

        var source = snapshot.SourceGeometry;
        var scale = geometry.BodyLogicalRect.Width / source.BodyPixelWidth;
        if (scale <= 0)
        {
            return false;
        }

        var hit = false;
        var bodyMask = snapshot.AlphaMasks[snapshot.BodyId];
        var bodyX = (int)Math.Floor((logicalPoint.X - geometry.BodyLogicalRect.X) / scale);
        var bodyY = (int)Math.Floor((logicalPoint.Y - geometry.BodyLogicalRect.Y) / scale);
        if (bodyX >= 0 && bodyY >= 0 && bodyX < source.BodyPixelWidth && bodyY < source.BodyPixelHeight)
        {
            hit |= bodyMask[(bodyY * source.BodyPixelWidth) + bodyX] >= MinVisibleAlpha;
        }

        var overlayMask = snapshot.AlphaMasks[expressionAssetId];
        var overlay = geometry.OverlayLogicalRect;
        var overlayX = (int)Math.Floor((logicalPoint.X - overlay.X) / scale);
        var overlayY = (int)Math.Floor((logicalPoint.Y - overlay.Y) / scale);
        if (overlayX >= 0 && overlayY >= 0
            && overlayX < source.OverlayPixelWidth && overlayY < source.OverlayPixelHeight)
        {
            hit |= overlayMask[(overlayY * source.OverlayPixelWidth) + overlayX] >= MinVisibleAlpha;
        }

        return hit;
    }
}