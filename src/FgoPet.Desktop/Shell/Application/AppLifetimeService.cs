using System.Windows;
using Application = System.Windows.Application;

namespace FgoPet.App.Lifetime;

/// <summary>Coordinates window visibility, show/hide, and exit on behalf of the tray.</summary>
public sealed class AppLifetimeService : IAppLifetime
{
    private readonly Application _application;
    private readonly Func<Task>? _normalExit;
    private Window? _petWindow;

    public AppLifetimeService(Application application, Func<Task>? normalExit = null)
    {
        _application = application;
        _normalExit = normalExit;
    }

    public void Shutdown(int exitCode) => _application.Shutdown(exitCode);

    public void RequestNormalExit()
    {
        if (_normalExit is null) _application.Shutdown(0);
        else _ = _normalExit(); // The root coordinator observes failures and serializes repeated exit requests.
    }

    public bool IsPetVisible => _petWindow?.IsVisible == true;

    public void ShowOrHidePet()
    {
        if (_petWindow is null)
        {
            return;
        }

        if (_petWindow.IsVisible)
        {
            _petWindow.Hide();
        }
        else
        {
            _petWindow.Show();
            _petWindow.Activate();
        }
    }

    public void AttachPetWindow(Window window) => _petWindow = window;
}
