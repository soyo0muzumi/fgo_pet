using System.Windows;
using System.Windows.Media;
using FgoPet.RenderingProbe.Art;

namespace FgoPet.RenderingProbe.Rendering;

public sealed record DeviceSize(int Width, int Height);

public sealed record DevicePoint(int X, int Y);

public sealed record PortraitGeometry(
    Size LogicalSize,
    DeviceSize DeviceSize,
    Rect BodyLogicalRect,
    Int32Rect BodyDeviceRect,
    Rect OverlayLogicalRect,
    Int32Rect OverlayDeviceRect,
    Point BottomAnchor,
    DevicePoint BottomAnchorDevice,
    Point PanelAnchor,
    DevicePoint PanelAnchorDevice);

public static class PortraitLayout
{
    public static PortraitGeometry Calculate(ArtBundle bundle, double scale, DpiScale dpi)
    {
        if (scale <= 0) throw new ArgumentOutOfRangeException(nameof(scale));
        var body = bundle.Images[bundle.Composition.BodyId];
        var logicalSize = new Size(body.PixelWidth * scale, body.PixelHeight * scale);
        var bodyDevice = Rectangle(0, 0, body.PixelWidth, body.PixelHeight, scale, dpi);
        var overlay = bundle.Composition.OverlayOffset;
        var overlaySize = bundle.Composition.OverlaySize;
        var overlayDevice = Rectangle(
            overlay.X,
            overlay.Y,
            overlay.X + overlaySize.Width,
            overlay.Y + overlaySize.Height,
            scale,
            dpi);
        var panelDevice = PointAt(bundle.Composition.PanelAnchor.X, bundle.Composition.PanelAnchor.Y, scale, dpi);
        var bottomDevice = PointAt(body.PixelWidth / 2.0, body.PixelHeight, scale, dpi);

        return new PortraitGeometry(
            logicalSize,
            new DeviceSize(bodyDevice.Width, bodyDevice.Height),
            Logical(bodyDevice, dpi),
            bodyDevice,
            Logical(overlayDevice, dpi),
            overlayDevice,
            new Point(bottomDevice.X / dpi.DpiScaleX, bottomDevice.Y / dpi.DpiScaleY),
            bottomDevice,
            new Point(panelDevice.X / dpi.DpiScaleX, panelDevice.Y / dpi.DpiScaleY),
            panelDevice);
    }

    private static Int32Rect Rectangle(double left, double top, double right, double bottom, double scale, DpiScale dpi)
    {
        var x1 = Align(left * scale, dpi.DpiScaleX);
        var y1 = Align(top * scale, dpi.DpiScaleY);
        var x2 = Align(right * scale, dpi.DpiScaleX);
        var y2 = Align(bottom * scale, dpi.DpiScaleY);
        return new Int32Rect(x1, y1, x2 - x1, y2 - y1);
    }

    private static DevicePoint PointAt(double x, double y, double scale, DpiScale dpi) =>
        new(Align(x * scale, dpi.DpiScaleX), Align(y * scale, dpi.DpiScaleY));

    private static int Align(double logical, double dpi) => (int)Math.Round(logical * dpi);

    private static Rect Logical(Int32Rect rectangle, DpiScale dpi) => new(
        rectangle.X / dpi.DpiScaleX,
        rectangle.Y / dpi.DpiScaleY,
        rectangle.Width / dpi.DpiScaleX,
        rectangle.Height / dpi.DpiScaleY);
}
