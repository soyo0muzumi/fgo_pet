using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using FgoPet.Core.Geometry;

namespace FgoPet.App.Portraits;

/// <summary>
/// Stable two-layer layered portrait: one fixed body image and one replaceable
/// expression overlay placed on the shared logical geometry.
/// </summary>
public partial class PortraitView : UserControl
{
    private PortraitSnapshot? _snapshot;

    public PortraitView() => InitializeComponent();

    internal BitmapSource? BodySourceForTest => BodyImage.Source as BitmapSource;

    internal BitmapSource? ExpressionSourceForTest => ExpressionImage.Source as BitmapSource;

    internal double OverlayLeftForTest => Canvas.GetLeft(ExpressionImage);

    internal double OverlayTopForTest => Canvas.GetTop(ExpressionImage);

    internal double OverlayWidthForTest => ExpressionImage.Width;

    internal double OverlayHeightForTest => ExpressionImage.Height;

    public void Load(PortraitSnapshot snapshot, PortraitGeometry geometry)
    {
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        ArgumentNullException.ThrowIfNull(geometry);

        BodyImage.Source = snapshot.Body;
        ExpressionImage.Source = snapshot.Images[snapshot.DefaultExpressionId];

        Root.Width = geometry.LogicalSize.Width;
        Root.Height = geometry.LogicalSize.Height;
        Place(BodyImage, geometry.BodyLogicalRect);
        Place(ExpressionImage, geometry.OverlayLogicalRect);
    }

    public void SetExpression(string assetId)
    {
        var snapshot = _snapshot ?? throw new InvalidOperationException("Load must be called before SetExpression.");
        if (assetId == snapshot.BodyId || !snapshot.Images.TryGetValue(assetId, out var image))
        {
            throw new ArgumentException($"未知的表情素材 ID: {assetId}", nameof(assetId));
        }

        // Replace only the overlay source; the body visual and outer size stay fixed.
        ExpressionImage.Source = image;
    }

    private static void Place(FrameworkElement element, LogicalRect rectangle)
    {
        Canvas.SetLeft(element, rectangle.X);
        Canvas.SetTop(element, rectangle.Y);
        element.Width = rectangle.Width;
        element.Height = rectangle.Height;
    }
}