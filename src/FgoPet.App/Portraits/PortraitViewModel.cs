using CommunityToolkit.Mvvm.ComponentModel;
using FgoPet.Core.Geometry;
using FgoPet.Core.Portraits;

namespace FgoPet.App.Portraits;

/// <summary>
/// View state for the portrait: the loaded snapshot, its geometry density, and the
/// currently requested expression semantic. Actual activation flows through the
/// portrait controller (Task 8).
/// </summary>
public sealed partial class PortraitViewModel : ObservableObject
{
    [ObservableProperty]
    private PortraitSnapshot? _snapshot;

    [ObservableProperty]
    private PortraitGeometry? _geometry;

    [ObservableProperty]
    private ExpressionSemantic _semantic = ExpressionSemantic.Neutral;

    /// <summary>Fetches the resolved expression asset ID for the current semantics, if any.</summary>
    public string? CurrentExpressionAssetId =>
        Snapshot is null || !Snapshot.Images.ContainsKey(DefaultExpressionId) ? null : DefaultExpressionId;

    private string DefaultExpressionId => Snapshot?.DefaultExpressionId ?? string.Empty;
}