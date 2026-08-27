using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FgoPet.RenderingProbe.Rendering;

namespace FgoPet.RenderingProbe.Tests;

public sealed class WpfPortraitSurfaceTests
{
    [Fact]
    public void Surface_keeps_body_and_parent_when_expression_changes()
    {
        StaTest.Run(() =>
        {
            var bundle = PortraitLayoutTests.Bundle();
            var geometry = PortraitLayout.Calculate(bundle, 0.6, new DpiScale(1.25, 1.25));
            var surface = new WpfPortraitSurface();
            surface.Load(bundle);
            surface.ApplyGeometry(geometry);
            var canvas = Assert.IsType<Canvas>(surface.View);
            var body = Assert.IsType<Image>(canvas.Children[0]);
            var overlay = Assert.IsType<Image>(canvas.Children[1]);
            var originalBody = body.Source;
            var originalCanvas = surface.View;

            surface.SetExpression("r01c02");

            Assert.Same(originalCanvas, surface.View);
            Assert.Same(originalBody, body.Source);
            Assert.Same(bundle.Images["r01c02"], overlay.Source);
            Assert.Equal(geometry.OverlayLogicalRect.X, Canvas.GetLeft(overlay));
            Assert.Equal(geometry.OverlayLogicalRect.Width, overlay.Width);
        });
    }

    [Fact]
    public void Capture_preserves_physical_dimensions_and_visible_alpha()
    {
        StaTest.Run(() =>
        {
            var bundle = PortraitLayoutTests.Bundle();
            var dpi = new DpiScale(1.25, 1.25);
            var geometry = PortraitLayout.Calculate(bundle, 0.6, dpi);
            var surface = new WpfPortraitSurface();
            surface.Load(bundle);
            surface.ApplyGeometry(geometry);

            var capture = surface.Capture(dpi);
            var pixels = new byte[capture.PixelWidth * capture.PixelHeight * 4];
            capture.CopyPixels(pixels, capture.PixelWidth * 4, 0);

            Assert.Equal(geometry.DeviceSize.Width, capture.PixelWidth);
            Assert.Equal(geometry.DeviceSize.Height, capture.PixelHeight);
            Assert.Contains(pixels.Where((_, index) => index % 4 == 3), alpha => alpha > 0);
        });
    }
}
