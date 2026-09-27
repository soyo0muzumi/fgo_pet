using System.Collections.Immutable;
using FgoPet.Extensibility;

namespace FgoPet.Kernel.Lifecycle;

public interface IShutdownParticipant
{
    string Code { get; }
    Task StopAsync(CancellationToken cancellationToken);
}
public sealed record ShutdownResult(ImmutableArray<LifecycleError> Errors);

/// <summary>Normal async shutdown keeps the host alive until owned work and extensions have stopped.</summary>
public sealed class ShutdownCoordinator : IDisposable
{
    private readonly ProcessLifetime _lifetime;
    private readonly BackgroundOperationTracker _operations;
    private readonly ImmutableArray<IShutdownParticipant> _participants;
    private readonly Func<ValueTask> _releaseHost;
    private readonly Action _exit;
    private readonly PluginRuntime? _extensions;
    private readonly object _gate = new();
    private Task<ShutdownResult>? _stopTask;

    public ShutdownCoordinator(ProcessLifetime lifetime, BackgroundOperationTracker operations,
        IEnumerable<IShutdownParticipant> participants, Func<ValueTask> releaseHost, Action exit, PluginRuntime? extensions = null)
    {
        _lifetime = lifetime; _operations = operations; _participants = participants.ToImmutableArray();
        foreach (var participant in _participants) BackgroundOperationTracker.ValidateCode(participant.Code);
        _releaseHost = releaseHost; _exit = exit; _extensions = extensions;
    }

    public Task<PluginActivationResult> StartExtensionsAsync() => _extensions?.StartAsync(_lifetime.StoppingToken)
        ?? Task.FromResult(new PluginActivationResult(true, null, []));

    public Task<ShutdownResult> StopAsync()
    {
        TaskCompletionSource<ShutdownResult> completion;
        lock (_gate)
        {
            if (_stopTask is not null) return _stopTask;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _stopTask = completion.Task;
        }
        _ = CompleteStopAsync(completion);
        return completion.Task;
    }

    private async Task CompleteStopAsync(TaskCompletionSource<ShutdownResult> completion)
    {
        var errors = ImmutableArray.CreateBuilder<LifecycleError>();
        _extensions?.CloseAdmission();
        try { _lifetime.RequestStop(); }
        catch (Exception) { errors.Add(new("PROCESS_STOP_FAILED", "PROCESS")); }
        if (_lifetime.CancellationCallbackFailed) errors.Add(new("PROCESS_CANCELLATION_CALLBACK_FAILED", "PROCESS"));
        try { await _operations.DrainAsync(); }
        catch (Exception) { errors.Add(new("PROCESS_DRAIN_FAILED", "PROCESS")); }
        errors.AddRange(_operations.Failures);
        foreach (var participant in _participants)
        {
            try { await participant.StopAsync(CancellationToken.None); }
            catch (Exception) { errors.Add(new("SHUTDOWN_PARTICIPANT_FAILED", participant.Code)); }
        }
        if (_extensions is not null)
        {
            try
            {
                foreach (var failure in await _extensions.StopAsync()) errors.Add(new(failure.Code, "PLUGIN"));
            }
            catch (Exception) { errors.Add(new("EXTENSIONS_STOP_FAILED", "PLUGIN")); }
        }
        try { await _releaseHost(); }
        catch (Exception) { errors.Add(new("HOST_RELEASE_FAILED", "HOST")); }
        try { _exit(); }
        catch (Exception) { errors.Add(new("APPLICATION_EXIT_FAILED", "HOST")); }
        completion.TrySetResult(new(errors.ToImmutable()));
    }

    /// <summary>Synchronous emergency exit only fences/cancels; it never starts asynchronous draining.</summary>
    public void Dispose() => _lifetime.RequestStop();
}
