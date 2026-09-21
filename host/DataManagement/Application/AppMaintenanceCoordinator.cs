using FgoPet.Core.Agents;

namespace FgoPet.App.Privacy;

/// <summary>Serializes state maintenance and stops Agent polling before file replacement.</summary>
public sealed class AppMaintenanceCoordinator : IAppMaintenanceCoordinator
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IAgentRelayRuntime? _agentRuntime;

    public AppMaintenanceCoordinator(IAgentRelayRuntime? agentRuntime = null) => _agentRuntime = agentRuntime;

    public async Task<IAsyncDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_agentRuntime is not null)
            {
                await _agentRuntime.StopAsync(cancellationToken).ConfigureAwait(false);
            }

            return new Lease(_gate);
        }
        catch
        {
            _gate.Release();
            throw;
        }
    }

    private sealed class Lease(SemaphoreSlim gate) : IAsyncDisposable
    {
        private int _released;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                gate.Release();
            }

            return ValueTask.CompletedTask;
        }
    }
}
