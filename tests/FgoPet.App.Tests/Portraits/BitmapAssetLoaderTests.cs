using System.IO;
using System.Windows.Media.Imaging;
using FgoPet.App.Portraits;
using FgoPet.Core.Packs;
using FgoPet.Infrastructure.Packs;
using Xunit;

namespace FgoPet.App.Tests.Portraits;

public sealed class BitmapAssetLoaderTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "fgo-pet-loader-" + Guid.NewGuid().ToString("N"));

    public BitmapAssetLoaderTests() => Directory.CreateDirectory(_temp);

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
    public void LoadValidated_freezes_images_and_builds_alpha_masks()
    {
        StaTest.Run(() =>
        {
            var body = AppearanceBundle.CreatePng(303, 603, alpha: 255);
            var expression = AppearanceBundle.CreatePng(256, 240, alpha: 200);
            var bundle = AppearanceBundle.Write(_temp, body, expression);

            var snapshot = Load(bundle);

            Assert.Equal(2, snapshot.Images.Count);
            Assert.Equal("full_body", snapshot.BodyId);
            Assert.Equal("r01c01", snapshot.DefaultExpressionId);
            Assert.All(snapshot.Images.Values, image => Assert.True(image.IsFrozen));
            Assert.Equal(303, snapshot.SourceGeometry.BodyPixelWidth);
            Assert.Equal(603, snapshot.SourceGeometry.BodyPixelHeight);
            Assert.Equal(303 * 603, snapshot.AlphaMasks["full_body"].Length);
            Assert.Equal(256 * 240, snapshot.AlphaMasks["r01c01"].Length);
            Assert.Equal(200, snapshot.AlphaMasks["r01c01"][0]);
            Assert.Equal(255, snapshot.AlphaMasks["full_body"][0]);
        });
    }

    [Fact]
    public void LoadValidated_releases_file_handles()
    {
        StaTest.Run(() =>
        {
            var bundle = AppearanceBundle.Write(
                _temp,
                AppearanceBundle.CreatePng(303, 603, alpha: 255),
                AppearanceBundle.CreatePng(256, 240, alpha: 200));

            var snapshot = Load(bundle);

            var bodyPath = Path.Combine(_temp, "runtime", "full_body.png");
            var expressionPath = Path.Combine(_temp, "runtime", "expressions", "r01c01.png");
            File.WriteAllBytes(bodyPath, AppearanceBundle.CreatePng(303, 603, alpha: 255));
            File.WriteAllBytes(expressionPath, AppearanceBundle.CreatePng(256, 240, alpha: 200));
            Assert.NotNull(snapshot);
        });
    }

    [Fact]
    public void LoadValidated_rejects_invisible_alpha()
    {
        StaTest.Run(() =>
        {
            var bundle = AppearanceBundle.Write(
                _temp,
                AppearanceBundle.CreatePng(303, 603, alpha: 255),
                AppearanceBundle.CreatePng(256, 240, alpha: 0));

            var failure = Assert.Throws<PackFailureException>(() => Load(bundle));
            Assert.Equal(PackErrorCode.ImageHasNoVisibleAlpha, failure.Failure.Code);
        });
    }

    [Fact]
    public void LoadValidated_rejects_a_manipulated_expression()
    {
        StaTest.Run(() =>
        {
            // Expression pixel size differs from the composition's overlay_size.
            var bundle = AppearanceBundle.Write(
                _temp,
                AppearanceBundle.CreatePng(303, 603, alpha: 255),
                AppearanceBundle.CreatePng(255, 239, alpha: 200));

            var failure = Assert.Throws<PackFailureException>(() => Load(bundle));
            Assert.Equal(PackErrorCode.CompositionOutOfBounds, failure.Failure.Code);
        });
    }

    [Fact]
    public void LoadValidated_rejects_overlay_out_of_bounds()
    {
        StaTest.Run(() =>
        {
            var bundle = AppearanceBundle.Write(
                _temp,
                AppearanceBundle.CreatePng(303, 603, alpha: 255),
                AppearanceBundle.CreatePng(256, 240, alpha: 200),
                overlayX: 300);

            var failure = Assert.Throws<PackFailureException>(() => Load(bundle));
            Assert.Equal(PackErrorCode.CompositionOutOfBounds, failure.Failure.Code);
        });
    }

    [Fact]
    public void LoadValidated_rejects_a_panel_anchor_out_of_bounds()
    {
        StaTest.Run(() =>
        {
            var bundle = AppearanceBundle.Write(
                _temp,
                AppearanceBundle.CreatePng(303, 603, alpha: 255),
                AppearanceBundle.CreatePng(256, 240, alpha: 200),
                panelX: 400);

            var failure = Assert.Throws<PackFailureException>(() => Load(bundle));
            Assert.Equal(PackErrorCode.CompositionOutOfBounds, failure.Failure.Code);
        });
    }

    [Fact]
    public void LoadValidated_rejects_a_corrupt_image_file()
    {
        StaTest.Run(() =>
        {
            var bundle = AppearanceBundle.Write(
                _temp,
                AppearanceBundle.CreatePng(303, 603, alpha: 255),
                new byte[64] { 0xA5, 0x5A, 0xA5, 0x5A, 0xA5, 0x5A, 0xA5, 0x5A, 0xA5, 0x5A, 0xA5, 0x5A, 0xA5, 0x5A, 0xA5, 0x5A, 0xA5, 0x5A, 0xA5, 0x5A, 0xA5, 0x5A, 0xA5, 0x5A, 0xA5, 0x5A, 0xA5, 0x5A, 0xA5, 0x5A, 0xA5, 0x5A, 0xA5, 0x5A, 0xA5, 0x5A, 0xA5, 0x5A, 0xA5, 0x5A, 0xA5, 0x5A, 0xA5, 0x5A, 0xA5, 0x5A, 0xA5, 0x5A, 0xA5, 0x5A, 0xA5, 0x5A, 0xA5, 0x5A, 0xA5, 0x5A, 0xA5, 0x5A, 0xA5, 0x5A, 0xA5, 0x5A, 0xA5, 0x5A });

            var failure = Assert.Throws<PackFailureException>(() => Load(bundle));
            Assert.Equal(PackErrorCode.ImageDecodeFailed, failure.Failure.Code);
        });
    }

    private static PortraitSnapshot Load((string Root, string ManifestPath) bundle)
    {
        var manifest = AppearanceManifestReader.Read(bundle.ManifestPath);
        var validated = AppearanceValidator.Validate(manifest, bundle.Root).Value
            ?? throw new InvalidOperationException("Bundle must be validated before loading.");
        return BitmapAssetLoader.LoadValidated(validated);
    }
}