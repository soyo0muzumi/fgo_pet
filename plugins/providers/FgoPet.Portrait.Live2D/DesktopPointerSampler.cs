using System.Windows;

namespace FgoPet.App.Portraits.Live2D;

internal readonly record struct DesktopPointerSample(double X, double Y, bool Active);

/// <summary>Normalizes physical screen coordinates; window movement is not cursor activity.</summary>
internal sealed class DesktopPointerSampler
{
    private Point? _lastActivePosition;

    public bool TrySample(Point cursor, Point topLeft, Point bottomRight, out DesktopPointerSample sample)
    {
        sample = default;
        var width = bottomRight.X - topLeft.X;
        var height = bottomRight.Y - topLeft.Y;
        if (!double.IsFinite(cursor.X) || !double.IsFinite(cursor.Y)
            || !double.IsFinite(topLeft.X) || !double.IsFinite(topLeft.Y)
            || !double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0) return false;
        var active = _lastActivePosition is not { } last || (cursor - last).Length >= 4;
        if (active) _lastActivePosition = cursor;
        sample = new DesktopPointerSample((cursor.X - topLeft.X) / width, (cursor.Y - topLeft.Y) / height, active);
        return true;
    }
}
