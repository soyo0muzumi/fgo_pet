using System.Windows;

namespace FgoPet.App.Lifetime;

/// <summary>
/// Process/application lifetime seam. Task 10 extends this with show/hide, tray, and
/// normal-exit flows; Task 5 only needs a bounded shutdown for the smoke test.
/// </summary>
public interface IAppLifetime
{
    void Shutdown(int exitCode);
}

public sealed class ApplicationLifetime : IAppLifetime
{
    public void Shutdown(int exitCode) => Application.Current?.Shutdown(exitCode);
}