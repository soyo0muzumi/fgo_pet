using FgoPet.Core.Packs;

namespace FgoPet.Core.Geometry;

/// <summary>Device pixels per DIP, potentially differing on the X and Y axes.</summary>
public readonly record struct Dpi2(double X, double Y);

public readonly record struct DevicePoint(int X, int Y);

public readonly record struct DeviceSize(int Width, int Height);

public readonly record struct DeviceRect(int X, int Y, int Width, int Height)
{
    public int Left => X;
    public int Top => Y;
    public int Right => X + Width;
    public int Bottom => Y + Height;
}

public readonly record struct LogicalPoint(double X, double Y);

public readonly record struct LogicalSize(double Width, double Height);

public readonly record struct LogicalRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
}

/// <summary>
/// Pure pixel geometry of a layered portrait, independent of any rendering framework.
/// Every source edge is rounded to integer device pixels exactly once.
/// </summary>
public sealed record PortraitSourceGeometry(
    int BodyPixelWidth,
    int BodyPixelHeight,
    int OverlayPixelX,
    int OverlayPixelY,
    int OverlayPixelWidth,
    int OverlayPixelHeight,
    int PanelAnchorX,
    int PanelAnchorY)
{
    public static PortraitSourceGeometry FromManifest(AppearanceManifestV3 manifest, int bodyPixelWidth, int bodyPixelHeight)
    {
        var composition = manifest.Composition;
        return new PortraitSourceGeometry(
            bodyPixelWidth,
            bodyPixelHeight,
            composition.OverlayOffset.X,
            composition.OverlayOffset.Y,
            composition.OverlaySize.Width,
            composition.OverlaySize.Height,
            composition.PanelAnchor.X,
            composition.PanelAnchor.Y);
    }
}

public sealed record PortraitGeometry(
    LogicalSize LogicalSize,
    DeviceSize DeviceSize,
    LogicalRect BodyLogicalRect,
    DeviceRect BodyDeviceRect,
    LogicalRect OverlayLogicalRect,
    DeviceRect OverlayDeviceRect,
    LogicalPoint BottomAnchor,
    DevicePoint BottomAnchorDevice,
    LogicalPoint PanelAnchor,
    DevicePoint PanelAnchorDevice);