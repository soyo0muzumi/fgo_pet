using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FgoPet.RenderingProbe.Art;
using SkiaSharp;

namespace FgoPet.RenderingProbe.Rendering;

public sealed class SkiaPortraitSurface : IRenderSurface, IDisposable
{
    private readonly Image _view = new() { Stretch = Stretch.Fill, SnapsToDevicePixels = true, UseLayoutRounding = true };
    private readonly Dictionary<string, SKImage> _images = new(StringComparer.Ordinal);
    private ArtBundle? _bundle;
    private PortraitGeometry? _geometry;
    private string? _expressionId;

    public FrameworkElement View => _view;

    public void Load(ArtBundle bundle)
    {
        DisposeImages();
        _bundle = bundle;
        foreach (var (stableId, bitmap) in bundle.Images)
        {
            _images.Add(stableId, Decode(bitmap));
        }
        _expressionId = bundle.Composition.DefaultExpressionId;
        RefreshView();
    }

    public void SetExpression(string id)
    {
        var bundle = _bundle ?? throw new InvalidOperationException("Load must be called before SetExpression.");
        if (id == bundle.Composition.BodyId || !_images.ContainsKey(id))
        {
            throw new ArgumentException($"Unknown expression ID: {id}", nameof(id));
        }
        _expressionId = id;
        RefreshView();
    }

    public void ApplyGeometry(PortraitGeometry geometry)
    {
        _geometry = geometry;
        _view.Width = geometry.LogicalSize.Width;
        _view.Height = geometry.LogicalSize.Height;
        RefreshView();
    }

    public BitmapSource Capture(DpiScale dpi)
    {
        var bundle = _bundle ?? throw new InvalidOperationException("Load must be called before Capture.");
        var geometry = _geometry ?? throw new InvalidOperationException("ApplyGeometry must be called before Capture.");
        var expressionId = _expressionId ?? throw new InvalidOperationException("No expression is selected.");
        var info = new SKImageInfo(geometry.DeviceSize.Width, geometry.DeviceSize.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info) ?? throw new InvalidOperationException("Could not create Skia surface.");
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        var sampling = new SKSamplingOptions(SKCubicResampler.Mitchell);
        Draw(canvas, _images[bundle.Composition.BodyId], geometry.BodyDeviceRect, sampling);
        Draw(canvas, _images[expressionId], geometry.OverlayDeviceRect, sampling);
        canvas.Flush();
        using var snapshot = surface.Snapshot();
        using var data = snapshot.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = new MemoryStream(data.ToArray(), writable: false);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    public void Dispose()
    {
        DisposeImages();
        GC.SuppressFinalize(this);
    }

    private void RefreshView()
    {
        if (_bundle is not null && _geometry is not null && _expressionId is not null)
        {
            _view.Source = Capture(new DpiScale(
                _geometry.DeviceSize.Width / _geometry.LogicalSize.Width,
                _geometry.DeviceSize.Height / _geometry.LogicalSize.Height));
        }
    }

    private static void Draw(SKCanvas canvas, SKImage image, Int32Rect rectangle, SKSamplingOptions sampling)
    {
        var destination = SKRect.Create(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);
        canvas.DrawImage(image, destination, sampling, null);
    }

    private static SKImage Decode(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        using var data = SKData.CreateCopy(stream.ToArray());
        return SKImage.FromEncodedData(data) ?? throw new InvalidDataException("Skia could not decode a bundle image.");
    }

    private void DisposeImages()
    {
        foreach (var image in _images.Values) image.Dispose();
        _images.Clear();
        _view.Source = null;
    }
}
