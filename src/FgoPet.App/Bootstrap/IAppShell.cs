namespace FgoPet.App.Bootstrap;

/// <summary>Starts and owns the production desktop composition after bootstrap checks.</summary>
public interface IAppShell
{
    Task StartAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}
