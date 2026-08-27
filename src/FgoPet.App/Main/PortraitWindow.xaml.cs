using System.Windows;
using FgoPet.App.Portraits;
using FgoPet.Core.Geometry;

namespace FgoPet.App.Main;

/// <summary>
/// Transparent, borderless, always-on-top portrait host window. The exact DPI,
/// placement, hit-testing, and lifecycle behavior is completed in Tasks 8-10.
/// </summary>
public partial class PortraitWindow : Window
{
    public PortraitWindow() => InitializeComponent();

    public PortraitView PortraitView => Portrait;

    /// <summary>Loads a validated snapshot at the given window/portrait geometry.</summary>
    public void Present(PortraitSnapshot snapshot, PortraitGeometry geometry)
    {
        Portrait.Load(snapshot, geometry);
        Width = geometry.LogicalSize.Width;
        Height = geometry.LogicalSize.Height;
    }
}