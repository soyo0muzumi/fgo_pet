using System.Windows;
using System.Windows.Media;
using FgoPet.RenderingProbe.Rendering;

namespace FgoPet.RenderingProbe.Tests;

public sealed class SkiaPortraitSurfaceTests
{
    [Fact]
    public void Surface_matches_shared_dimensions_alpha_and_expression_switching()
    {
        StaTest.Run(() =>
        {
            var bundle = PortraitLayoutTests.Bundle();
            var dpi = new DpiScale(1.25, 1.25);
            var geometry = PortraitLayout.Calculate(bundle, 0.6, dpi);
            using var surface = new SkiaPortraitSurface();
            surface.Load(bundle);
            surface.ApplyGeometry(geometry);

            var first = surface.Capture(dpi);
            surface.SetExpression("r01c02");
            var second = surface.Capture(dpi);
            surface.Load(bundle);

            Assert.Equal(geometry.DeviceSize.Width, first.PixelWidth);
            Assert.Equal(geometry.DeviceSize.Height, first.PixelHeight);
            Assert.Equal(first.PixelWidth, second.PixelWidth);
            Assert.True(HasVisibleAlpha(first));
            Assert.True(HasVisibleAlpha(second));
        });
    }

    private static bool HasVisibleAlpha(System.Windows.Media.Imaging.BitmapSource bitmap)
    {
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels.Where((_, index) => index % 4 == 3).Any(alpha => alpha > 0);
    }
}
