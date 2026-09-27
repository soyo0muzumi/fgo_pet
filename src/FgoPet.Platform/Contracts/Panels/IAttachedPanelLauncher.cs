namespace FgoPet.Core.Panels;

/// <summary>
/// Host attached-panel entry point exposed to module-owned windows.
/// The dialogue window uses it to expand the desktop panel and switch it to the
/// focus surface. Modules depend on this contract instead of the concrete
/// <c>AttachedPanelViewModel</c> so the Desktop assemblies stay acyclic.
/// </summary>
public interface IAttachedPanelLauncher
{
    /// <summary>Current panel state; <see cref="AttachedPanelState.Collapsed"/> means the panel must be expanded first.</summary>
    AttachedPanelState State { get; }

    /// <summary>Expands or collapses the panel, as if the portrait were clicked.</summary>
    void PortraitClick();

    /// <summary>Switches the panel to its focus surface.</summary>
    void FocusClick();
}
