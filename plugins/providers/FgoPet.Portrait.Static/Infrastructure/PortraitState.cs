using System.Windows;
using FgoPet.UiSdk;
using FgoPet.App.Windowing;
using FgoPet.Core.Geometry;
using FgoPet.Core.Portraits;

namespace FgoPet.App.Portraits;

/// <summary>
/// The complete immutable portrait state that is published only after a full snapshot
/// and geometry succeed. Client code replaces the whole state atomically.
/// </summary>
public sealed record PortraitState(
    PortraitSelection Selection,
    ExpressionSemantic Semantic,
    string ExpressionAssetId,
    double Scale,
    PortraitSnapshot Snapshot,
    PortraitGeometry Geometry) : IPortraitFrame
{
    public void Present(FrameworkElement view)
    {
        if (view is not PortraitView portrait) throw new ArgumentException("Incompatible static portrait content.", nameof(view));
        portrait.Load(Snapshot, Geometry);
        portrait.SetExpression(ExpressionAssetId);
    }
    public bool IsHit(Point portraitLocalPoint) =>
        AlphaHitTestService.IsHit(portraitLocalPoint, Snapshot, ExpressionAssetId, Geometry);
}