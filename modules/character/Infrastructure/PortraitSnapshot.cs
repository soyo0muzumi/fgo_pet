using System.Windows.Media.Imaging;
using FgoPet.Core.Geometry;
using FgoPet.Core.Windowing;

namespace FgoPet.App.Portraits;

/// <summary>
/// An immutable, fully-validated portrait bundle: frozen images plus the precomputed
/// source Alpha masks used for transparent hit testing.
/// </summary>
public sealed record PortraitSnapshot(
    IReadOnlyDictionary<string, BitmapSource> Images,
    string BodyId,
    string DefaultExpressionId,
    IReadOnlyDictionary<string, byte[]> AlphaMasks,
    PortraitSourceGeometry SourceGeometry) : IAlphaMaskSource
{
    public BitmapSource Body => Images[BodyId];
}