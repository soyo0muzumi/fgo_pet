using System.Windows;

namespace FgoPet.App.Lifetime;

/// <summary>
/// Process/application lifetime seam. Owns the pet window visibility and the tray
/// driven show/hide and normal exit.
/// </summary>
public interface IAppLifetime
{
    void Shutdown(int exitCode);

    void RequestNormalExit();

    void ShowOrHidePet();

    bool IsPetVisible { get; }

    void AttachPetWindow(Window window);
}