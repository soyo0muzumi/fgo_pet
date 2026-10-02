using FgoPet.Core.Geometry;
using FgoPet.Core.Windowing;

namespace FgoPet.App.Windowing;

internal static class TransientSurfacePlacement
{
    public static DeviceRect Place(DeviceRect anchor, DeviceRect workArea, Dpi2 dpi,
        double widthDip, double heightDip)
    {
        if (!double.IsFinite(dpi.X) || dpi.X <= 0 || !double.IsFinite(dpi.Y) || dpi.Y <= 0)
            throw new ArgumentOutOfRangeException(nameof(dpi));
        var width = Math.Min(workArea.Width, Math.Max(1, (int)Math.Ceiling(widthDip * dpi.X)));
        var height = Math.Min(workArea.Height, Math.Max(1, (int)Math.Ceiling(heightDip * dpi.Y)));
        var gap = Math.Max(1, (int)Math.Round(12 * dpi.X));
        var right = anchor.Right + gap;
        var left = anchor.Left - gap - width;
        var x = right + width <= workArea.Right ? right
            : left >= workArea.Left ? left : right;
        return ScreenLayout.ClampFullyVisible(new DeviceRect(x, anchor.Top, width, height), workArea);
    }
}
