using FgoPet.RenderingProbe.Rendering;
using FgoPet.RenderingProbe.Windowing;

namespace FgoPet.RenderingProbe.Tests;

public sealed class ProbeOptionsTests
{
    [Theory]
    [InlineData("0.5", 0.5)]
    [InlineData("0.6", 0.6)]
    [InlineData("0.75", 0.75)]
    public void Parse_accepts_supported_scales(string value, double expected)
    {
        var manifest = Path.GetFullPath("manifest.json");
        var options = ProbeOptions.Parse([
            "--bundle", manifest,
            "--renderer", "skia",
            "--transparency", "dwm",
            "--scale", value,
            "--output", "captures",
        ]);

        Assert.Equal(manifest, options.BundlePath);
        Assert.Equal(RenderBackend.Skia, options.Backend);
        Assert.Equal(TransparencyMode.Dwm, options.Transparency);
        Assert.Equal(expected, options.Scale);
        Assert.True(Path.IsPathFullyQualified(options.OutputDirectory));
    }

    [Theory]
    [InlineData("0.4")]
    [InlineData("0.61")]
    [InlineData("1")]
    public void Parse_rejects_unsupported_scales(string value)
    {
        Assert.Throws<ArgumentException>(() => ProbeOptions.Parse([
            "--bundle", Path.GetFullPath("manifest.json"),
            "--renderer", "wpf",
            "--transparency", "conventional",
            "--scale", value,
            "--output", "captures",
        ]));
    }

    [Fact]
    public void Parse_requires_absolute_bundle_path()
    {
        Assert.Throws<ArgumentException>(() => ProbeOptions.Parse([
            "--bundle", "manifest.json",
            "--renderer", "wpf",
            "--transparency", "conventional",
            "--scale", "0.6",
            "--output", "captures",
        ]));
    }
}
