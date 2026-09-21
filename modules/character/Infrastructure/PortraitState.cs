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
    PortraitGeometry Geometry);