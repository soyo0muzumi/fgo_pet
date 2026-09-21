namespace FgoPet.Core.Geometry;

public enum PanelSide
{
    Left,
    Right,
}

public sealed record PanelPlacement(DeviceRect Bounds, PanelSide Side, bool FlippedHorizontally, bool ClampedVertically);

/// <summary>
/// Places an attached panel beside the portrait's panel anchor. The panel hangs below
/// the anchor, is clamped into the work area, capped to 60% of the work-area height,
/// prefers the left side, flips right when the left side does not fit, and otherwise
/// chooses the side that occludes the portrait rect least.
/// </summary>
public static class AttachedPanelLayout
{
    public static PanelPlacement Place(DevicePoint anchor, DeviceSize desired, DeviceRect workArea, DeviceRect portrait)
    {
        if (desired.Width <= 0 || desired.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(desired));
        }
        if (workArea.Width <= 0 || workArea.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(workArea));
        }

        var width = Math.Min(desired.Width, workArea.Width);
        var height = Math.Min(desired.Height, (int)Math.Floor(workArea.Height * 0.6));

        var top = anchor.Y;
        var clampedVertically = false;
        if (top + height > workArea.Bottom)
        {
            top = workArea.Bottom - height;
            clampedVertically = true;
        }
        if (top < workArea.Top)
        {
            top = workArea.Top;
            clampedVertically = true;
        }

        var leftCandidate = new DeviceRect(anchor.X - width, top, width, height);
        var rightCandidate = new DeviceRect(anchor.X, top, width, height);
        var leftFits = leftCandidate.X >= workArea.X;
        var rightFits = rightCandidate.Right <= workArea.Right;

        var preferRight = !leftFits
            || (rightFits
                && IntersectArea(rightCandidate, portrait) < IntersectArea(leftCandidate, portrait));

        DeviceRect bounds;
        if (!preferRight)
        {
            bounds = leftCandidate;
        }
        else
        {
            bounds = rightFits
                ? rightCandidate
                : new DeviceRect(Math.Max(workArea.X, workArea.Right - width), top, width, height);
        }

        return new PanelPlacement(bounds, preferRight ? PanelSide.Right : PanelSide.Left, preferRight, clampedVertically);
    }

    private static int IntersectArea(DeviceRect a, DeviceRect b)
    {
        var x1 = Math.Max(a.X, b.X);
        var y1 = Math.Max(a.Y, b.Y);
        var x2 = Math.Min(a.Right, b.Right);
        var y2 = Math.Min(a.Bottom, b.Bottom);
        return Math.Max(0, x2 - x1) * Math.Max(0, y2 - y1);
    }
}