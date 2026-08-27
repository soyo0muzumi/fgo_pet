using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FgoPet.RenderingProbe.Art;

namespace FgoPet.RenderingProbe.Rendering;

public sealed class WpfPortraitSurface : IRenderSurface
{
    private readonly Canvas _canvas = new() { Background = Brushes.Transparent, SnapsToDevicePixels = true, UseLayoutRounding = true };
    private readonly Image _body = Image();
    private readonly Image _overlay = Image();
    private ArtBundle? _bundle;
    private PortraitGeometry? _geometry;

    public WpfPortraitSurface()
    {
        _canvas.Children.Add(_body);
        _canvas.Children.Add(_overlay);
    }

    public FrameworkElement View => _canvas;

    public void Load(ArtBundle bundle)
    {
        _bundle = bundle;
        _body.Source = bundle.Images[bundle.Composition.BodyId];
        _overlay.Source = bundle.Images[bundle.Composition.DefaultExpressionId];
    }

    public void SetExpression(string id)
    {
        var bundle = _bundle ?? throw new InvalidOperationException("Load must be called before SetExpression.");
        if (id == bundle.Composition.BodyId || !bundle.Images.TryGetValue(id, out var image))
        {
            throw new ArgumentException($"Unknown expression ID: {id}", nameof(id));
        }
        _overlay.Source = image;
    }

    public void ApplyGeometry(PortraitGeometry geometry)
    {
        _geometry = geometry;
        _canvas.Width = geometry.LogicalSize.Width;
        _canvas.Height = geometry.LogicalSize.Height;
        Place(_body, geometry.BodyLogicalRect);
        Place(_overlay, geometry.OverlayLogicalRect);
    }

    public BitmapSource Capture(DpiScale dpi)
    {
        var geometry = _geometry ?? throw new InvalidOperationException("ApplyGeometry must be called before Capture.");
        _canvas.Measure(geometry.LogicalSize);
        _canvas.Arrange(new Rect(geometry.LogicalSize));
        _canvas.UpdateLayout();
        var capture = new RenderTargetBitmap(
            geometry.DeviceSize.Width,
            geometry.DeviceSize.Height,
            96 * dpi.DpiScaleX,
            96 * dpi.DpiScaleY,
            PixelFormats.Pbgra32);
        capture.Render(_canvas);
        capture.Freeze();
        return capture;
    }

    private static Image Image()
    {
        var image = new Image { Stretch = Stretch.Fill, SnapsToDevicePixels = true, UseLayoutRounding = true };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        return image;
    }

    private static void Place(FrameworkElement element, Rect rectangle)
    {
        Canvas.SetLeft(element, rectangle.X);
        Canvas.SetTop(element, rectangle.Y);
        element.Width = rectangle.Width;
        element.Height = rectangle.Height;
    }
}
