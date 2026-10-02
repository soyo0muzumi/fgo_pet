namespace FgoPet.App.Privacy;

public interface IAppMaintenanceCoordinator
{
    Task<IAsyncDisposable> EnterAsync(CancellationToken cancellationToken);
    void CompleteStartup();
}
