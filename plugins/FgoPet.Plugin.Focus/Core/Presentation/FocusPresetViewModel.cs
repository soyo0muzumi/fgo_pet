using FgoPet.Core.Focus;

namespace FgoPet.App.Panels;

/// <summary>Built-in presets are code constants, not mutable database rows.</summary>
public static class FocusPresetCatalog
{
    public static readonly FocusPreset Short = FocusPreset.Create(25, 5, 4);
    public static readonly FocusPreset Long = FocusPreset.Create(50, 10, 2);
}
