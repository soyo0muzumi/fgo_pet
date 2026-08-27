using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FgoPet.RenderingProbe.Art;
using FgoPet.RenderingProbe.Rendering;

namespace FgoPet.RenderingProbe.Tests;

public sealed class PortraitLayoutTests
{
    [Theory]
    [InlineData(1.25, 227, 452)]
    [InlineData(1.5, 273, 543)]
    public void Calculate_uses_one_transform_for_body_overlay_and_anchors(double dpiValue, int deviceWidth, int deviceHeight)
    {
        var geometry = PortraitLayout.Calculate(Bundle(), 0.6, new DpiScale(dpiValue, dpiValue));

        Assert.Equal(181.8, geometry.LogicalSize.Width, 6);
        Assert.Equal(361.8, geometry.LogicalSize.Height, 6);
        Assert.Equal(deviceWidth, geometry.DeviceSize.Width);
        Assert.Equal(deviceHeight, geometry.DeviceSize.Height);
        Assert.Equal(Math.Round(13 * 0.6 * dpiValue), geometry.OverlayDeviceRect.X);
        Assert.Equal(
            Math.Round(269 * 0.6 * dpiValue),
            geometry.OverlayDeviceRect.X + geometry.OverlayDeviceRect.Width);
        Assert.Equal(Math.Round(151 * 0.6 * dpiValue), geometry.PanelAnchorDevice.X);
        Assert.Equal(Math.Round(360 * 0.6 * dpiValue), geometry.PanelAnchorDevice.Y);
        Assert.Equal(deviceHeight, geometry.BottomAnchorDevice.Y);
    }

    internal static ArtBundle Bundle()
    {
        var images = new Dictionary<string, BitmapSource>
        {
            ["full_body"] = Bitmap(303, 603, Colors.Blue),
            ["r01c01"] = Bitmap(256, 240, Colors.Red),
            ["r01c02"] = Bitmap(256, 240, Colors.Green),
        };
        return new ArtBundle(
            Path.GetFullPath("manifest.json"),
            new ArtComposition("full_body", "r01c01", new ArtPoint(13, 0), new ArtSize(256, 240), new ArtPoint(151, 360), 0.6),
            images);
    }

    private static BitmapSource Bitmap(int width, int height, Color color)
    {
        var pixels = new byte[width * height * 4];
        for (var index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = color.B;
            pixels[index + 1] = color.G;
            pixels[index + 2] = color.R;
            pixels[index + 3] = color.A;
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }
}
