namespace FgoPet.Core.Windowing;

/// <summary>
/// Where the pet window was when last visible, in DIP coordinates relative to the
/// monitor work area, plus the saved monitor ID and DPI so restoration can rebuild
/// device geometry per monitor.
/// </summary>
public sealed record WindowPlacement(
    string? MonitorId,
    double OffsetX,
    double OffsetY,
    double SavedDpiX,
    double SavedDpiY,
    double WindowWidthDip,
    double WindowHeightDip);