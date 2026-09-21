namespace FgoPet.Core.Geometry;

/// <summary>
/// Computes all logical and device geometry for a layered portrait from one shared
/// source-pixel-to-device-pixel transform (Phase 0 ADR). Rectangle edges are aligned
/// in device space; logical values are derived from the aligned device rectangles.
/// </summary>
public static class PortraitLayout
{
    public static PortraitGeometry Calculate(PortraitSourceGeometry source, double scale, Dpi2 dpi)
    {
        if (scale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scale));
        }

        var logicalSize = new LogicalSize(source.BodyPixelWidth * scale, source.BodyPixelHeight * scale);
        var bodyDevice = Rectangle(
            0, 0,
            source.BodyPixelWidth, source.BodyPixelHeight,
            scale, dpi);
        var overlayDevice = Rectangle(
            source.OverlayPixelX, source.OverlayPixelY,
            source.OverlayPixelX + source.OverlayPixelWidth,
            source.OverlayPixelY + source.OverlayPixelHeight,
            scale, dpi);
        var panelDevice = PointAt(source.PanelAnchorX, source.PanelAnchorY, scale, dpi);
        var bottomDevice = PointAt(source.BodyPixelWidth / 2.0, source.BodyPixelHeight, scale, dpi);

        return new PortraitGeometry(
            logicalSize,
            new DeviceSize(bodyDevice.Width, bodyDevice.Height),
            ToLogical(bodyDevice, dpi),
            bodyDevice,
            ToLogical(overlayDevice, dpi),
            overlayDevice,
            ToLogicalPoint(bottomDevice, dpi),
            bottomDevice,
            ToLogicalPoint(panelDevice, dpi),
            panelDevice);
    }

    private static DeviceRect Rectangle(
        double left,
        double top,
        double right,
        double bottom,
        double scale,
        Dpi2 dpi)
    {
        var x1 = Align(left * scale, dpi.X);
        var y1 = Align(top * scale, dpi.Y);
        var x2 = Align(right * scale, dpi.X);
        var y2 = Align(bottom * scale, dpi.Y);
        return new DeviceRect(x1, y1, x2 - x1, y2 - y1);
    }

    private static DevicePoint PointAt(double x, double y, double scale, Dpi2 dpi) =>
        new(Align(x * scale, dpi.X), Align(y * scale, dpi.Y));

    private static int Align(double logical, double dpi) => (int)Math.Round(logical * dpi);

    private static LogicalRect ToLogical(DeviceRect rectangle, Dpi2 dpi) => new(
        rectangle.X / dpi.X,
        rectangle.Y / dpi.Y,
        rectangle.Width / dpi.X,
        rectangle.Height / dpi.Y);

    private static LogicalPoint ToLogicalPoint(DevicePoint point, Dpi2 dpi) => new(
        point.X / dpi.X,
        point.Y / dpi.Y);
}