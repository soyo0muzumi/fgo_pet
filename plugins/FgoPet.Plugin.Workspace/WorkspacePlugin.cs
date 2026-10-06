using FgoPet.Extensibility;

namespace FgoPet.Plugin.Workspace;

/// <summary>Catalog composition for host-authorized workspace capabilities.</summary>
public sealed class WorkspacePlugin : IFgoPetPlugin, IDisposable
{
    public PluginManifest Manifest { get; } = new("workspace", "1.0.0", 1, []);
    public PluginContributions Contributions { get; }

    public WorkspacePlugin(IWorkspaceAccessGuard guard)
    {
        ArgumentNullException.ThrowIfNull(guard);
        Contributions = PluginContributions.Empty with
        {
            Tools = [.. WorkspaceReadTools.Create(guard), .. WorkspaceWriteTools.Create(guard)]
        };
    }

    public ValueTask StartAsync(CancellationToken stoppingToken)
    {
        stoppingToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose() { }
}
