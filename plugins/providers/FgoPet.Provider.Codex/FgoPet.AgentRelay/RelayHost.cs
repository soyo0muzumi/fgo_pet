using System.IO.Pipes;
using FgoPet.AgentRelay.Pipes;
using FgoPet.AgentRelay.Registration;
using FgoPet.AgentRelay.Routing;
using FgoPet.AgentRelay.Storage;

namespace FgoPet.AgentRelay;

public sealed class RelayHost
{
    private readonly TimeProvider _time;

    public RelayHost(IRelayStateStore? stateStore = null, TimeProvider? timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        Store = new RelayStore(stateStore);
        Registration = new RegistrationService(Store);
        Router = new RelayRouter(Store, Registration);
    }

    public RelayStore Store { get; }
    public RegistrationService Registration { get; }
    public RelayRouter Router { get; }

    /// <summary>Runs both current-user-only listeners and tears down the sibling if either fails.</summary>
    public async Task RunAsync(string adapterPipeName, string appPipeName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterPipeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(appPipeName);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var adapter = new AdapterPipeServer(Router, adapterPipeName, Registration, _time);
        var app = new AppPipeServer(Router, appPipeName, Registration, _time);
        NamedPipeServerStream? adapterListener = null;
        NamedPipeServerStream? appListener = null;
        Task adapterTask;
        Task appTask;
        try
        {
            // Bind both names before either listener starts accepting clients. If the second
            // bind fails, the first handle is disposed and no half-started Relay remains.
            adapterListener = adapter.CreateListener();
            appListener = app.CreateListener();
            adapterTask = adapter.RunAsync(lifetime.Token, adapterListener);
            adapterListener = null;
            appTask = app.RunAsync(lifetime.Token, appListener);
            appListener = null;
        }
        catch
        {
            lifetime.Cancel();
            adapterListener?.Dispose();
            appListener?.Dispose();
            throw;
        }
        var all = Task.WhenAll(adapterTask, appTask);
        try
        {
            // Do not wait for a sibling listener after an initial bind failure.
            await Task.WhenAny(adapterTask, appTask).ConfigureAwait(false);
            lifetime.Cancel();
            await all.ConfigureAwait(false);
        }
        finally
        {
            lifetime.Cancel();
            try { await Task.WhenAll(adapterTask, appTask).ConfigureAwait(false); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        }
    }
}
