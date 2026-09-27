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
/// Neutral layered-portrait composition, in source pixels. Deliberately carries no
/// pack-schema type: the geometry layer must not depend on the character/pack domain
/// (R2 in findings/35-split-path-blockers.md). Callers map their own manifest shape onto it.
/// </summary>
public readonly record struct PortraitComposition(
    int OverlayOffsetX,
    int OverlayOffsetY,
    int OverlayWidth,
    int OverlayHeight,
    int PanelAnchorX,
    int PanelAnchorY);

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
    public static PortraitSourceGeometry FromComposition(
        PortraitComposition composition,
        int bodyPixelWidth,
        int bodyPixelHeight) => new(
            bodyPixelWidth,
            bodyPixelHeight,
            composition.OverlayOffsetX,
            composition.OverlayOffsetY,
            composition.OverlayWidth,
            composition.OverlayHeight,
            composition.PanelAnchorX,
            composition.PanelAnchorY);
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