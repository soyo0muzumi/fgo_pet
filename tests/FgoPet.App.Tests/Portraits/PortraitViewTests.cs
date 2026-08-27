using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using FgoPet.App.Portraits;
using FgoPet.Core.Geometry;
using FgoPet.Infrastructure.Packs;
using Xunit;

namespace FgoPet.App.Tests.Portraits;

public sealed class PortraitViewTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "fgo-pet-view-" + Guid.NewGuid().ToString("N"));

    public PortraitViewTests() => Directory.CreateDirectory(_temp);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
            // best effort cleanup
        }
    }

    [Fact]
    public void Load_draws_body_and_overlay_on_the_shared_geometry()
    {
        StaTest.Run(() =>
        {
            var snapshot = Snapshot(twoExpressions: false);
            var geometry = PortraitLayout.Calculate(snapshot.SourceGeometry, 0.50, new Dpi2(2.0, 2.0));

            var view = new PortraitView();
            view.Load(snapshot, geometry);

            Assert.Same(snapshot.Body, view.BodySourceForTest);
            Assert.Same(snapshot.Images[snapshot.DefaultExpressionId], view.ExpressionSourceForTest);
            Assert.Equal(geometry.OverlayLogicalRect.X, view.OverlayLeftForTest, precision: 4);
            Assert.Equal(geometry.OverlayLogicalRect.Y, view.OverlayTopForTest, precision: 4);
            Assert.Equal(geometry.OverlayLogicalRect.Width, view.OverlayWidthForTest, precision: 4);
            Assert.Equal(geometry.OverlayLogicalRect.Height, view.OverlayHeightForTest, precision: 4);
        });
    }

    [Fact]
    public void SetExpression_replaces_only_the_overlay_and_keeps_body_and_size_stable()
    {
        StaTest.Run(() =>
        {
            var snapshot = Snapshot(twoExpressions: true);
            var geometry = PortraitLayout.Calculate(snapshot.SourceGeometry, 0.50, new Dpi2(2.0, 2.0));

            var view = new PortraitView();
            view.Load(snapshot, geometry);
            var originalBody = view.BodySourceForTest;
            var logical = new Size(geometry.LogicalSize.Width, geometry.LogicalSize.Height);
            view.Measure(logical);
            view.Arrange(new Rect(logical));
            view.UpdateLayout();
            var originalSize = view.RenderSize;

            view.SetExpression("r01c02");

            Assert.Same(originalBody, view.BodySourceForTest);
            Assert.Equal(originalSize.Width, view.RenderSize.Width);
            Assert.Equal(originalSize.Height, view.RenderSize.Height);
            Assert.Same(snapshot.Images["r01c02"], view.ExpressionSourceForTest);
        });
    }

    [Fact]
    public void SetExpression_rejects_a_body_id_or_unknown_id()
    {
        StaTest.Run(() =>
        {
            var snapshot = Snapshot(twoExpressions: true);
            var geometry = PortraitLayout.Calculate(snapshot.SourceGeometry, 0.50, new Dpi2(2.0, 2.0));

            var view = new PortraitView();
            view.Load(snapshot, geometry);

            Assert.Throws<ArgumentException>(() => view.SetExpression("full_body"));
            Assert.Throws<ArgumentException>(() => view.SetExpression("missing"));
        });
    }

    [Fact]
    public void SetExpression_requires_load_first()
    {
        StaTest.Run(() =>
        {
            var view = new PortraitView();
            Assert.Throws<InvalidOperationException>(() => view.SetExpression("r01c01"));
        });
    }

    private PortraitSnapshot Snapshot(bool twoExpressions)
    {
        var bundle = AppearanceBundle.Write(
            _temp,
            AppearanceBundle.CreatePng(303, 603, alpha: 255),
            AppearanceBundle.CreatePng(256, 240, alpha: 200),
            expressionPng2: twoExpressions ? AppearanceBundle.CreatePng(256, 240, alpha: 200) : null);
        var manifest = AppearanceManifestReader.Read(bundle.ManifestPath);
        var validated = AppearanceValidator.Validate(manifest, bundle.Root).Value!;
        return BitmapAssetLoader.LoadValidated(validated);
    }
}