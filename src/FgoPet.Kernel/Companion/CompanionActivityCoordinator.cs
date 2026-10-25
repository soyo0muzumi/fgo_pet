using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FgoPet.Core.Dialogue;
using FgoPet.Extensibility;

namespace FgoPet.Kernel.Companion;

/// <summary>Coalesces host observations into one bounded background judgment, never a chat send or action run.</summary>
public sealed class CompanionActivityCoordinator : IAsyncDisposable, IDisposable
{
    private sealed record Note(CompanionActivityConfiguration Configuration, CompanionObservation Observation);
    private readonly ICompanionActivityHost _host;
    private readonly ICompanionActivityModel _model;
    private readonly ICompanionActivityBudget _budget;
    private readonly IDialogueContextLifetime _lifetime;
    private readonly TimeProvider _clock;
    private readonly Action<CompanionActivityDiagnostic>? _diagnostics;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private CancellationToken _processStopping;
    private CancellationTokenSource? _activeCancellation;
    private Task? _loop;
    private Task? _stop;
    private bool _closed;
    private bool _pending;
    private long _epoch;
    private long _changes;
    private string _reason = "context_changed";
    private string? _lastAttempt;
    private long? _lastDispatchTimestamp;
    private Note? _note;

    public CompanionActivityCoordinator(ICompanionActivityHost host, ICompanionActivityModel model,
        ICompanionActivityBudget budget, IDialogueContextLifetime lifetime, TimeProvider clock,
        Action<CompanionActivityDiagnostic>? diagnostics = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _budget = budget ?? throw new ArgumentNullException(nameof(budget));
        _lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _diagnostics = diagnostics;
    }

    public string? LastFailureCode { get; private set; }

    public void NotifyChanged(string reason = "context_changed")
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 64
            || reason.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('_' or '.' or '-')))
            throw new ArgumentException("A fixed, content-free activity reason is required.", nameof(reason));
        lock (_gate)
        {
            if (_closed) return;
            _changes++;
            _pending = true;
            _reason = reason;
        }
    }

    /// <summary>Call on scope/permission/model changes. A running judgment can no longer publish its result.</summary>
    public void Invalidate()
    {
        CancellationTokenSource? active;
        lock (_gate)
        {
            _epoch++;
            _pending = false;
            _note = null;
            _lastAttempt = null;
            active = _activeCancellation;
        }
        Cancel(active);
    }

    /// <summary>Project the current ephemeral interpretation without making it a fact or another activity's source.</summary>
    public CompanionObservation? GetObservation(CompanionContextSnapshot currentSnapshot)
    {
        ArgumentNullException.ThrowIfNull(currentSnapshot);
        Note? note;
        lock (_gate) note = _closed ? null : _note;
        var now = _clock.GetUtcNow();
        if (note is null || note.Observation.ValidUntilUtc <= now
            || note.Configuration.Context.Scope != currentSnapshot.Scope
            || note.Configuration.Context.Revision != currentSnapshot.Revision
            || !CompanionActivityContext.Fresh(note.Configuration.Context, now)) return null;
        foreach (var source in note.Configuration.Context.Observations)
            if (!currentSnapshot.Observations.Any(item => !item.IsInterpretation && item.SourceId == source.SourceId
                && item.Version == source.Version && item.ValidUntilUtc > now)) return null;
        try
        {
            CompanionObservation? result = null;
            if (!_host.TryCommit(note.Configuration, () =>
            {
                lock (_gate)
                    if (!_closed && ReferenceEquals(note, _note)) result = note.Observation;
            })) return null;
            return result;
        }
        catch { return null; }
    }

    public void Start(CancellationToken processStopping)
    {
        lock (_gate)
        {
            if (_closed) throw new InvalidOperationException("COMPANION_ACTIVITY_STOPPED");
            if (_loop is not null) throw new InvalidOperationException("COMPANION_ACTIVITY_ALREADY_STARTED");
            _processStopping = processStopping;
            _loop = Task.Run(RunAsync);
        }
    }

    public async Task PulseAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate) if (_closed || !_pending) return;
        using var admitted = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token, _processStopping);
        var token = admitted.Token;
        if (!await _serial.WaitAsync(0, token).ConfigureAwait(false)) return;
        try
        {
            long capturedChanges;
            lock (_gate)
            {
                if (_closed || !_pending) return;
                capturedChanges = _changes;
            }
            CompanionActivityConfiguration? captured;
            try { captured = _host.Capture(); }
            catch { Failed("", "COMPANION_ACTIVITY_HOST_UNAVAILABLE"); return; }
            if (captured is null) return;
            CompanionActivityConfiguration configuration;
            try { configuration = CompanionActivityContext.Freeze(captured, _clock.GetUtcNow()); }
            catch { Failed("", "COMPANION_ACTIVITY_INVALID_CONTEXT"); return; }
            var attempt = AttemptKey(configuration);
            string reason;
            long epoch;
            lock (_gate)
            {
                if (_closed || !_pending) return;
                if (_lastAttempt == attempt) { _pending = _changes != capturedChanges; return; }
                if (_lastDispatchTimestamp is { } timestamp
                    && _clock.GetElapsedTime(timestamp, _clock.GetTimestamp()) < configuration.Limits.MinInterval) return;
                reason = _reason;
                epoch = _epoch;
            }
            using var lease = _lifetime.TryAcquireBackground(token);
            if (lease is null) return;
            using var timeout = new CancellationTokenSource(configuration.Limits.Timeout, _clock);
            using var work = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token, lease.Token);
            lock (_gate)
            {
                if (_closed || epoch != _epoch) return;
                _pending = _changes != capturedChanges;
                _activeCancellation = work;
            }
            var request = new CompanionActivityRequest(Guid.NewGuid().ToString("N"), reason,
                _clock.GetUtcNow(), configuration);
            var dispatched = 0;
            var charged = false;
            var route = ModelRouteKey.From(configuration.Connection);
            try
            {
                EnsureCurrent(work.Token);
                Log(request.ActivityId, "started");
                var dispatch = new DispatchGate((chat, modelRoute, start, dispatchToken) =>
                {
                    EnsureCurrent(dispatchToken);
                    if (Interlocked.CompareExchange(ref dispatched, 1, 0) != 0 || modelRoute != route
                        || chat.ServantId != configuration.Context.Scope.RoleId
                        || chat.ConversationId != configuration.Context.Scope.ConversationId
                        || chat.Tools is not null || chat.MaxOutputTokens is not > 0
                        || chat.MaxOutputTokens > configuration.Limits.MaxOutputTokens
                        || !chat.Metadata.TryGetValue("fgo_auxiliary", out var purpose) || purpose != "companion_activity"
                        || !chat.Metadata.TryGetValue("fgo_activity_id", out var activity) || activity != request.ActivityId)
                        throw new InvalidOperationException("COMPANION_ACTIVITY_DISPATCH_REJECTED");
                    object? value = null;
                    var accepted = lease.Commit(() => _host.TryCommit(configuration, () =>
                    {
                        dispatchToken.ThrowIfCancellationRequested();
                        EnsureLocal(epoch, work.Token);
                        if (!CompanionActivityContext.Fresh(configuration.Context, _clock.GetUtcNow()))
                            throw new OperationCanceledException("COMPANION_ACTIVITY_STALE_SOURCE", work.Token);
                        // Reserve the full input allowance conservatively; this is not claimed provider usage.
                        if (!_budget.TryReserve(new(request.ActivityId, configuration.Context.Scope, route,
                            configuration.PolicyRevision, _clock.GetUtcNow(), configuration.Limits.MaxInputTokens,
                            chat.MaxOutputTokens.Value))) throw new InvalidOperationException("COMPANION_ACTIVITY_BUDGET_UNAVAILABLE");
                        charged = true;
                        lock (_gate)
                        {
                            _lastAttempt = attempt;
                            _lastDispatchTimestamp = _clock.GetTimestamp();
                        }
                        dispatchToken.ThrowIfCancellationRequested();
                        value = start();
                    }));
                    if (!accepted) throw new OperationCanceledException("COMPANION_ACTIVITY_CONTEXT_CHANGED", work.Token);
                    Log(request.ActivityId, "dispatched");
                    return value;
                });
                var judgment = await _model.EvaluateAsync(request, dispatch, EnsureCurrent, work.Token).ConfigureAwait(false);
                EnsureCurrent(work.Token);
                if (!charged) throw new InvalidOperationException("COMPANION_ACTIVITY_DISPATCH_MISSING");
                var observation = CompanionActivityContext.CreateObservation(request, judgment, _clock.GetUtcNow());
                var committed = lease.Commit(() => _host.TryCommit(configuration, () =>
                {
                    EnsureLocal(epoch, work.Token);
                    lock (_gate)
                    {
                        if (_closed || epoch != _epoch) throw new OperationCanceledException(work.Token);
                        _note = observation is null ? null : new(configuration, observation);
                        LastFailureCode = null;
                    }
                }));
                if (!committed) throw new OperationCanceledException("COMPANION_ACTIVITY_CONTEXT_CHANGED", work.Token);
                Log(request.ActivityId, observation is null ? "no_change" : "completed");
            }
            catch (OperationCanceledException)
            {
                Log(request.ActivityId, timeout.IsCancellationRequested && !lease.Token.IsCancellationRequested
                    ? "timed_out" : "cancelled");
            }
            catch (Exception error)
            {
                var code = error is InvalidOperationException && IsCode(error.Message)
                    ? error.Message : "COMPANION_ACTIVITY_FAILED";
                Failed(request.ActivityId, code);
            }
            finally
            {
                lock (_gate) if (ReferenceEquals(_activeCancellation, work)) _activeCancellation = null;
            }

            void EnsureCurrent(CancellationToken currentToken)
            {
                currentToken.ThrowIfCancellationRequested();
                EnsureLocal(epoch, work.Token);
                lease.CheckCurrent();
                if (!CompanionActivityContext.Fresh(configuration.Context, _clock.GetUtcNow())
                    || !_host.TryCommit(configuration, static () => { }))
                    throw new OperationCanceledException("COMPANION_ACTIVITY_CONTEXT_CHANGED", work.Token);
            }
        }
        finally { _serial.Release(); }
    }

    private void EnsureLocal(long epoch, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (_gate)
            if (_closed || epoch != _epoch) throw new OperationCanceledException("COMPANION_ACTIVITY_INVALIDATED", token);
    }

    private async Task RunAsync()
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, _processStopping);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5), _clock);
        try
        {
            do
            {
                try { await PulseAsync(linked.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (linked.IsCancellationRequested) { break; }
                catch { Failed("", "COMPANION_ACTIVITY_WORKER_FAILED"); }
            } while (await timer.WaitForNextTickAsync(linked.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
    }

    public Task StopAsync()
    {
        lock (_gate)
        {
            _closed = true;
            _pending = false;
            _note = null;
            return _stop ??= StopCoreAsync();
        }
    }

    private async Task StopCoreAsync()
    {
        try { await _shutdown.CancelAsync().ConfigureAwait(false); }
        catch { /* A cancellation callback cannot prevent the owned task from being drained. */ }
        Task? loop;
        lock (_gate) loop = _loop;
        if (loop is not null) await loop.ConfigureAwait(false);
        await _serial.WaitAsync().ConfigureAwait(false);
        _serial.Release();
    }

    public ValueTask DisposeAsync() => new(StopAsync());
    public void Dispose() => _ = Observe(StopAsync());

    private static string AttemptKey(CompanionActivityConfiguration configuration)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            configuration.Context.Scope, configuration.Context.Revision, configuration.PolicyRevision,
            route = ModelRouteKey.From(configuration.Connection)
        }))));

    private void Failed(string activity, string code) { LastFailureCode = code; Log(activity, "failed", code); }
    private void Log(string activity, string stage, string? code = null)
    {
        try { _diagnostics?.Invoke(new(_clock.GetUtcNow(), activity, stage, code)); }
        catch { /* Metadata diagnostics cannot change activity execution. */ }
    }

    private static bool IsCode(string value) => value.Length is > 0 and <= 96
        && value.All(character => char.IsAsciiLetterUpper(character) || char.IsAsciiDigit(character) || character == '_');
    private static void Cancel(CancellationTokenSource? cancellation)
    {
        if (cancellation is null) return;
        try { _ = Observe(cancellation.CancelAsync()); }
        catch (ObjectDisposedException) { }
    }
    private static async Task Observe(Task task) { try { await task.ConfigureAwait(false); } catch { } }

    private sealed class DispatchGate(Func<ChatRequest, ModelRouteKey, Func<object?>, CancellationToken, object?> dispatch) : IChatDispatchGate
    {
        public T Dispatch<T>(ChatRequest request, ModelRouteKey route, Func<T> start, CancellationToken cancellationToken)
            => (T)dispatch(request, route, () => start(), cancellationToken)!;
    }
}
