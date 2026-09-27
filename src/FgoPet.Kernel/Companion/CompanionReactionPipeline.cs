using FgoPet.App.Runtime;
using FgoPet.Extensibility;
using FgoPet.Kernel.Lifecycle;

namespace FgoPet.Kernel.Companion;

/// <summary>Owns semantic reactions, role cancellation and process-tracked content work.</summary>
public sealed class CompanionReactionPipeline : IDisposable
{
    private readonly PluginCatalog _catalog;
    private readonly PluginRuntime _plugins;
    private readonly CompanionSignalPipeline _signals;
    private readonly AppRuntime _identity;
    private readonly CompanionPresentation _presentation;
    private readonly BackgroundOperationTracker _operations;
    private readonly ProcessLifetime _process;
    private readonly object _gate = new();
    private CancellationTokenSource _roleLifetime = new();
    private long _generation;
    private bool _closed;
    public string? LastFailureCode { get; private set; }

    public CompanionReactionPipeline(PluginCatalog catalog, PluginRuntime plugins, CompanionSignalPipeline signals,
        AppRuntime identity, CompanionPresentation presentation, BackgroundOperationTracker operations, ProcessLifetime process)
    {
        _catalog = catalog; _plugins = plugins; _signals = signals; _identity = identity;
        _presentation = presentation; _operations = operations; _process = process;
        signals.Signal += OnSignal;
        identity.ActiveRoleChanged += OnRoleChanged;
    }
    private void OnSignal(CompanionSignal signal)
    {
        lock (_gate)
        {
            var role = _identity.ActiveRole;
            if (_closed || _process.IsStopping || role is null || role.ServantId != signal.RoleId) return;
            var generation = ++_generation;
            var scope = new CompanionContentScope(role.PackageId, role.PackageVersion, role.AppearanceId, role.ServantId);
            _ = ObserveAsync(_operations.RunAsync("COMPANION_CONTENT", async token =>
            {
                foreach (var entry in _catalog.FeedbackProviders)
                {
                    if (!_plugins.IsActive(entry.PluginId)) continue;
                    CompanionFeedback? feedback;
                    try { feedback = await entry.Provider.GetFeedbackAsync(signal, scope, token).WaitAsync(token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                    catch (Exception) { LastFailureCode = "COMPANION_CONTENT_FAILED"; continue; }
                    lock (_gate)
                    {
                        if (_closed || token.IsCancellationRequested || generation != _generation
                            || !ReferenceEquals(role, _identity.ActiveRole) || !_plugins.IsActive(entry.PluginId)) return;
                        if (feedback is not null && _presentation.Apply(role, feedback.Expression, feedback.Text)) return;
                    }
                }
            }, _roleLifetime.Token));
        }
    }
    private static async Task ObserveAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception) { /* Process tracker records safe fault codes; cancellation is expected. */ }
    }
    private void OnRoleChanged(object? sender, AppStateChangedEventArgs<ActiveRoleState> args)
    {
        CancellationTokenSource previous;
        lock (_gate)
        {
            if (_closed) return;
            previous = _roleLifetime;
            _roleLifetime = new();
            _generation++;
        }
        CancelAndDispose(previous);
    }
    private static void CancelAndDispose(CancellationTokenSource source)
    {
        try { source.Cancel(); }
        catch (AggregateException) { /* External cancellation callbacks cannot break the identity transition. */ }
        finally { source.Dispose(); }
    }
    public void Dispose()
    {
        CancellationTokenSource previous;
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            _signals.Signal -= OnSignal;
            _identity.ActiveRoleChanged -= OnRoleChanged;
            previous = _roleLifetime;
            _generation++;
        }
        CancelAndDispose(previous);
    }
}
