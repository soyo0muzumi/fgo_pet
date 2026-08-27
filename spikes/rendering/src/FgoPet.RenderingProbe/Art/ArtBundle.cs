using System.Windows.Media.Imaging;

namespace FgoPet.RenderingProbe.Art;

public sealed record ArtPoint(int X, int Y);

public sealed record ArtSize(int Width, int Height);

public sealed record ArtComposition(
    string BodyId,
    string DefaultExpressionId,
    ArtPoint OverlayOffset,
    ArtSize OverlaySize,
    ArtPoint PanelAnchor,
    double DefaultScale);

public sealed record ArtBundle(
    string ManifestPath,
    ArtComposition Composition,
    IReadOnlyDictionary<string, BitmapSource> Images);
