using System.Text.Json;
using System.Text.Encodings.Web;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Settings;
using FgoPet.Extensibility;
using FgoPet.Kernel.Companion;
using FgoPet.Platform.WindowText;

namespace FgoPet.App.Companion;

/// <summary>Host authority for ambient sources, explicit consent and the independent background cost ledger.</summary>
internal sealed class CompanionAwarenessHost : ICompanionAwarenessControls, ICompanionActivityHost,
    ICompanionActivityBudget, IDerivedConversationData, IAsyncDisposable, IDisposable
{
    private static readonly JsonSerializerOptions DataJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly object _gate;
    private readonly SemaphoreSlim _sampling = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CompanionAwarenessStore _store;
    private readonly Func<ToolScope?> _currentScope;
    private readonly Func<ModelConnectionSettings?> _connection;
    private readonly Func<ToolScope, CancellationToken, Task<IReadOnlyList<CompanionObservation>>> _readActivity;
    private readonly Func<CancellationToken, CompanionObservation> _readApplications;
    private readonly IWindowTextReader _windows;
    private readonly IDialogueContextLifetime _lifetime;
    private readonly TimeProvider _clock;
    private readonly CancellationTokenSource _stopping = new();
    private CancellationTokenSource _sources = new();
    private CompanionActivityCoordinator? _coordinator;
    private ToolScope? _scope;
    private ModelConnectionSettings? _model;
    private string? _route;
    private CompanionAwarenessPolicy _policy = new(new(), null);
    private CompanionContextSnapshot? _raw;
    private string? _lastSampleRevision;
    private IReadOnlyList<WindowTextCandidate> _candidates = [];
    private WindowTextCandidate? _allowedWindow;
    private string _contextToken = Guid.NewGuid().ToString("N");
    private long _revision;
    private bool _ready;
    private bool _closed;
    private bool _available;
    private string _activityStatus = "disabled";
    private DateTimeOffset? _lastActivity;
    private Task? _loop;
    private Task? _stop;
    private bool _notifyBaseline;

    public CompanionAwarenessHost(CompanionAwarenessStore store, Func<ToolScope?> currentScope,
        Func<ModelConnectionSettings?> connection,
        Func<ToolScope, CancellationToken, Task<IReadOnlyList<CompanionObservation>>> readActivity,
        Func<CancellationToken, CompanionObservation> readApplications, IWindowTextReader windows,
        IDialogueContextLifetime lifetime, TimeProvider clock, object? authorityGate = null)
    {
        _gate = authorityGate ?? new();
        _store = store; _currentScope = currentScope; _connection = connection;
        _readActivity = readActivity; _readApplications = readApplications; _windows = windows;
        _lifetime = lifetime; _clock = clock;
    }

    public void Attach(CompanionActivityCoordinator coordinator)
    {
        lock (_gate)
        {
            if (_coordinator is not null) throw new InvalidOperationException("AWARENESS_ALREADY_ATTACHED");
            _coordinator = coordinator;
        }
    }

    public void Start(CancellationToken processStopping)
    {
        lock (_gate)
        {
            if (_closed || _ready) return;
            _ready = true;
            _coordinator?.Start(processStopping);
            _loop = Task.Run(() => RunAsync(processStopping));
        }
    }

    public CompanionAwarenessStatus GetStatus()
    {
        lock (_gate) { RefreshIdentity(); return Status(); }
    }

    public bool ApplicationsAuthorized(ToolScope scope)
    {
        lock (_gate)
        {
            RefreshIdentity();
            return _ready && !_closed && _available && _scope == scope && Approved && _policy.Options.ApplicationsEnabled;
        }
    }

    public CompanionContextSnapshot? GetSnapshot(ToolScope scope)
    {
        CompanionContextSnapshot? snapshot;
        lock (_gate)
        {
            RefreshIdentity();
            snapshot = _scope == scope ? RawSnapshot() : null;
        }
        if (snapshot is null) return null;
        var note = _coordinator?.GetObservation(snapshot);
        lock (_gate)
        {
            RefreshIdentity();
            var current = RawSnapshot();
            if (current is null || current.Scope != scope || current.Revision != snapshot.Revision) return null;
            return note is null ? current : current with { Observations = current.Observations.Append(note).ToArray() };
        }
    }

    public CompanionActivityConfiguration? Capture()
    {
        lock (_gate)
        {
            RefreshIdentity();
            var raw = RawSnapshot();
            return !Approved || !_policy.Options.BackgroundEnabled || raw is null || raw.Observations.Count == 0 || _model is null
                ? null : new(raw, _model, PolicyRevision, new());
        }
    }

    public bool TryCommit(CompanionActivityConfiguration configuration, Action action)
    {
        lock (_gate)
        {
            RefreshIdentity();
            var raw = RawSnapshot();
            if (!Approved || !_policy.Options.BackgroundEnabled || raw is null || _model is null
                || configuration.Context.Scope != raw.Scope || configuration.Context.Revision != raw.Revision
                || configuration.PolicyRevision != PolicyRevision || ModelRouteKey.From(configuration.Connection) != ModelRouteKey.From(_model)
                || configuration.Context.Observations.Any(source => !raw.Observations.Any(current =>
                    !current.IsInterpretation && current.SourceId == source.SourceId && current.Version == source.Version
                    && current.Content == source.Content))) return false;
            action();
            return true;
        }
    }

    public bool TryReserve(CompanionActivityReservation reservation)
    {
        lock (_gate)
        {
            RefreshIdentity();
            return Approved && _policy.Options.BackgroundEnabled && reservation.Scope == _scope
                && reservation.PolicyRevision == PolicyRevision && _model is not null
                && reservation.Route == ModelRouteKey.From(_model)
                && _store.TryReserve(reservation, _policy.Options.DailyRequestLimit);
        }
    }

    public async ValueTask<CompanionAwarenessStatus> ConfigureAsync(string contextToken, long expectedRevision,
        CompanionAwarenessOptions options, CancellationToken token)
    {
        if (options is null || options.DailyRequestLimit is < 1 or > 24) throw new ArgumentOutOfRangeException(nameof(options));
        lock (_gate) Validate(contextToken, expectedRevision);
        using var suspension = await _lifetime.SuspendAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            Validate(contextToken, expectedRevision);
            if (_route is null && (options.BackgroundEnabled || options.ApplicationsEnabled)) throw Unavailable();
            var policy = new CompanionAwarenessPolicy(options, _route);
            _store.Save(CompanionAwarenessStore.ScopeKey(_scope!), policy);
            _policy = policy;
            ResetSources(clearWindow: !Approved);
            _revision++;
            _notifyBaseline = true;
            _activityStatus = options.BackgroundEnabled ? "waiting_for_change" : "disabled";
        }
        Wake();
        // The next baseline after an explicit save may run one bounded judgment.
        await SampleAsync(token, notifyInitial: true).ConfigureAwait(false);
        return GetStatus();
    }

    public async ValueTask<IReadOnlyList<CompanionAwarenessWindow>> ListWindowsAsync(string contextToken, CancellationToken token)
    {
        long revision;
        lock (_gate) { Validate(contextToken); if (!Approved) throw Unavailable(); revision = _revision; }
        await _sampling.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var candidates = await Task.Run(_windows.ListCandidates, token).ConfigureAwait(false);
            lock (_gate)
            {
                Validate(contextToken, revision);
                if (!Approved) throw Unavailable();
                _candidates = candidates.Take(128).ToArray();
                return _candidates.Select(Project).ToArray();
            }
        }
        finally { _sampling.Release(); }
    }

    public async ValueTask<CompanionAwarenessStatus> AllowWindowAsync(string contextToken, long expectedRevision,
        string candidateToken, CancellationToken token)
    {
        WindowTextCandidate selected;
        lock (_gate)
        {
            Validate(contextToken, expectedRevision);
            if (!Approved) throw Unavailable();
            selected = _candidates.SingleOrDefault(item => item.Token == candidateToken) ?? throw Unavailable();
        }
        using var suspension = await _lifetime.SuspendAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            Validate(contextToken, expectedRevision);
            if (!Approved || !_candidates.Contains(selected)) throw Unavailable();
            ResetSources(clearWindow: true);
            _allowedWindow = selected;
            _revision++;
            _notifyBaseline = true;
        }
        Wake();
        await SampleAsync(token, notifyInitial: true).ConfigureAwait(false);
        return GetStatus();
    }

    public async ValueTask<CompanionAwarenessStatus> ClearWindowsAsync(string contextToken, long expectedRevision, CancellationToken token)
    {
        lock (_gate) Validate(contextToken, expectedRevision);
        using var suspension = await _lifetime.SuspendAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            Validate(contextToken, expectedRevision);
            ResetSources(clearWindow: true);
            _revision++;
        }
        Wake();
        await SampleAsync(token).ConfigureAwait(false);
        return GetStatus();
    }

    /// <summary>Sampling never invokes a model. Only a changed semantic fingerprint schedules judgment.</summary>
    internal async Task SampleAsync(CancellationToken token, bool notifyInitial = false)
    {
        if (!await _sampling.WaitAsync(0, token).ConfigureAwait(false)) return;
        try
        {
            ToolScope scope;
            CompanionAwarenessOptions options;
            WindowTextCandidate? window;
            CancellationToken sourceToken;
            long revision;
            bool approved;
            lock (_gate)
            {
                RefreshIdentity();
                if (!_ready || _closed || !_available || _scope is null) return;
                scope = _scope; options = _policy.Options; approved = Approved;
                window = approved ? _allowedWindow : null;
                revision = _revision; sourceToken = _sources.Token;
            }
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, sourceToken, _stopping.Token);
            var observations = new List<CompanionObservation>();
            if (options.ActivityContextEnabled)
            {
                try { observations.AddRange(await _readActivity(scope, linked.Token).ConfigureAwait(false)); }
                catch (OperationCanceledException) when (linked.IsCancellationRequested) { throw; }
                catch { /* Failed sources are removed rather than represented by stale facts. */ }
            }
            if (approved && options.ApplicationsEnabled)
            {
                try { observations.Add(await Task.Run(() => _readApplications(linked.Token), linked.Token).ConfigureAwait(false)); }
                catch (OperationCanceledException) when (linked.IsCancellationRequested) { throw; }
                catch { }
            }
            var revokeWindow = false;
            if (window is not null)
            {
                try
                {
                    var read = await _windows.ReadAsync(window.Token, linked.Token).ConfigureAwait(false);
                    if (read.CanAttach && read.SourceTitle == window.Title && read.Application == window.Application)
                    {
                        var text = Limit(read.Text, 1200);
                        var content = SerializeData(new { application = Limit(read.Application, 80), visibleTextExcerpt = text,
                            window = Limit(read.SourceTitle, 100),
                            partial = read.Status == WindowTextReadStatus.Partial || text.Length != read.Text.Length });
                        observations.Add(Observe("desktop.window", content, read.ReadAtUtc, TimeSpan.FromMinutes(2)));
                    }
                    else revokeWindow = read.CanAttach || read.Status == WindowTextReadStatus.Unavailable;
                }
                catch (OperationCanceledException) when (linked.IsCancellationRequested) { throw; }
                catch { revokeWindow = true; }
            }
            linked.Token.ThrowIfCancellationRequested();
            lock (_gate)
            {
                RefreshIdentity();
                if (_closed || _scope != scope || _revision != revision || sourceToken.IsCancellationRequested) return;
                if (revokeWindow) { _allowedWindow = null; _candidates = []; _revision++; }
                var now = _clock.GetUtcNow();
                var valid = observations.Where(item => !item.IsInterpretation && item.ValidUntilUtc > now && item.ObservedAtUtc <= now)
                    .Take(8).OrderBy(item => item.SourceId, StringComparer.Ordinal).ToArray();
                var fingerprint = CompanionAwarenessStore.Hash(PolicyRevision + JsonSerializer.Serialize(valid.Select(item => new { item.SourceId, item.Version })));
                var changed = _lastSampleRevision is not null && _lastSampleRevision != fingerprint;
                _raw = new(scope, fingerprint, valid);
                _lastSampleRevision = fingerprint;
                if (changed || revokeWindow) _coordinator?.Invalidate();
                if (changed || notifyInitial || _notifyBaseline) _coordinator?.NotifyChanged(revokeWindow ? "window_unavailable" : "context_changed");
                _notifyBaseline = false;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested || _stopping.IsCancellationRequested) { }
        catch (OperationCanceledException) { /* A changed identity or permission owns the next sample. */ }
        finally { _sampling.Release(); }
    }

    public void ContextChanged()
    {
        lock (_gate)
        {
            if (_closed) return;
            RefreshIdentity();
            // A committed owner event invalidates old facts immediately, including an in-flight sample.
            ResetSources(clearWindow: false, preserveFingerprint: true);
        }
        Wake();
    }

    private void Wake()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { /* One pending sample coalesces all intervening owner events. */ }
    }

    private async Task RunAsync(CancellationToken processStopping)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(processStopping, _stopping.Token);
        try
        {
            while (!linked.IsCancellationRequested)
            {
                await SampleAsync(linked.Token).ConfigureAwait(false);
                await _wake.WaitAsync(TimeSpan.FromSeconds(15), linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
    }

    public void Record(CompanionActivityDiagnostic diagnostic)
    {
        lock (_gate)
        {
            _activityStatus = diagnostic.Stage == "failed" ? diagnostic.ErrorCode ?? "unavailable" : diagnostic.Stage;
            if (diagnostic.Stage is "completed" or "no_change") _lastActivity = diagnostic.Timestamp;
        }
    }

    private void RefreshIdentity()
    {
        ToolScope? scope = null;
        ModelConnectionSettings? model = null;
        string? route = null;
        if (_ready && !_closed)
        {
            try
            {
                scope = _currentScope(); model = _connection();
                if (model is not null) route = CompanionAwarenessStore.Hash(JsonSerializer.Serialize(ModelRouteKey.From(model)));
            }
            catch { scope = null; model = null; }
        }
        if (_scope == scope && _route == route)
        {
            if (_available && !_store.Available) { _available = false; ResetSources(clearWindow: true); _activityStatus = "storage_unavailable"; }
            return;
        }
        var modelChanged = _scope == scope && _route != route;
        _scope = scope; _model = model; _route = route;
        _contextToken = Guid.NewGuid().ToString("N"); _revision++;
        ResetSources(clearWindow: true);
        _available = false;
        _policy = new(new(), null);
        if (scope is not null)
        {
            try
            {
                var key = CompanionAwarenessStore.ScopeKey(scope);
                _policy = _store.Read(key);
                if (modelChanged && _policy.ApprovedRoute is not null)
                {
                    _policy = _policy with { ApprovedRoute = null };
                    _store.Save(key, _policy);
                }
                _available = true;
                _activityStatus = !_policy.Options.BackgroundEnabled ? "disabled" : Approved ? "waiting_for_change" : "approval_required";
                _lastActivity = null;
            }
            catch { _activityStatus = "storage_unavailable"; }
        }
    }

    private bool Approved => _available && _scope is not null && _route is not null && _route == _policy.ApprovedRoute;
    private string PolicyRevision => $"{_contextToken}.{_revision}";

    private CompanionContextSnapshot? RawSnapshot()
    {
        if (!_ready || _closed || !_available || _raw is null || _raw.Scope != _scope) return null;
        var now = _clock.GetUtcNow();
        var fresh = _raw.Observations.Where(item => item.ValidUntilUtc > now).ToArray();
        return fresh.Length == _raw.Observations.Count ? _raw : _raw with
        {
            Revision = CompanionAwarenessStore.Hash(_raw.Revision + JsonSerializer.Serialize(fresh.Select(item => item.SourceId))),
            Observations = fresh
        };
    }

    private CompanionAwarenessStatus Status()
    {
        var used = _available ? _store.Used(_clock.GetUtcNow()) : -1;
        if (_available && used < 0)
        {
            _available = false;
            ResetSources(clearWindow: true);
            _activityStatus = "storage_unavailable";
        }
        var recipient = _model is not null && Uri.TryCreate(_model.BaseUrl, UriKind.Absolute, out var endpoint)
            ? $"{_model.ModelId} via {endpoint.Host}:{endpoint.Port} ({_model.ProviderId})" : "No model configured";
        if (_model?.ProviderId == "custom-openai-compatible" && _model.ModelId == "buddy/deepseek-v4.1-flash"
            && Uri.TryCreate(_model.BaseUrl, UriKind.Absolute, out var buddy) && buddy.Host == "127.0.0.1" && buddy.Port == 8326
            && buddy.AbsolutePath.TrimEnd('/') == "/v1") recipient += " → Tencent CodeBuddy CN";
        return new(_contextToken, _revision, _ready && !_closed && _available && used >= 0, recipient, Approved,
            _policy.Options, used, _allowedWindow is null ? [] : [Project(_allowedWindow)],
            _activityStatus, _lastActivity);
    }

    private void Validate(string token, long? revision = null)
    {
        RefreshIdentity();
        if (!_ready || _closed || !_available || _scope is null || _contextToken != token
            || revision is not null && revision != _revision) throw Unavailable();
    }

    private void ResetSources(bool clearWindow, bool preserveFingerprint = false)
    {
        var old = _sources;
        _sources = new();
        // Tokens may still be held by a native sample. Dispose after its cancellation callbacks finish.
        _ = DisposeCancelled(old);
        _raw = null;
        if (!preserveFingerprint) _lastSampleRevision = null;
        if (clearWindow) { _allowedWindow = null; _candidates = []; }
        _coordinator?.Invalidate();
    }

    private static async Task DisposeCancelled(CancellationTokenSource source)
    { try { await source.CancelAsync().ConfigureAwait(false); } catch { } finally { source.Dispose(); } }

    internal static CompanionObservation Observe(string id, string content, DateTimeOffset at, TimeSpan lifetime)
        => new(id, CompanionAwarenessStore.Hash(content), at, at + lifetime, content);
    internal static string SerializeData<T>(T value) => JsonSerializer.Serialize(value, DataJson);
    internal static string Limit(string value, int length)
    {
        if (value.Length <= length) return value;
        if (length > 0 && char.IsHighSurrogate(value[length - 1]) && char.IsLowSurrogate(value[length])) length--;
        return value[..length];
    }
    private static CompanionAwarenessWindow Project(WindowTextCandidate candidate) => new(candidate.Token, candidate.Title, candidate.Application);
    private static InvalidOperationException Unavailable() => new("AWARENESS_STALE_OR_UNAVAILABLE");

    public void ForgetConversation(string conversationId, string roleId)
    {
        lock (_gate)
        {
            RefreshIdentity();
            if (_scope is { } scope && scope.ConversationId == conversationId && scope.RoleId == roleId)
            {
                ResetSources(clearWindow: true); _revision++;
                _store.ForgetPolicies(CompanionAwarenessStore.ScopeKey(scope));
                _policy = new(new(), null);
            }
        }
    }

    public void ForgetAll()
    {
        lock (_gate)
        {
            ResetSources(clearWindow: true); _revision++;
            _store.ForgetPolicies(); _policy = new(new(), null);
        }
    }

    internal bool ResetAfterRestore()
    {
        lock (_gate)
        {
            ResetSources(clearWindow: true); _revision++;
            _policy = new(new(), null); _available = false;
            return _store.RevokeForRestore();
        }
    }

    public JsonElement Export() => JsonSerializer.SerializeToElement(new { kind = "ambient_awareness", persistedSourceContent = false });
    // This owner has no tables in runtime.db and no source grants in private backups.
    public void NormalizeRestoredCopy(string validatedStagingDatabasePath) { }

    public ValueTask DisposeAsync()
    {
        lock (_gate) return new(_stop ??= StopCoreAsync());
    }

    public void Dispose() => _ = ObserveStop(DisposeAsync().AsTask());
    private static async Task ObserveStop(Task stop) { try { await stop.ConfigureAwait(false); } catch { } }

    private async Task StopCoreAsync()
    {
        Task? loop;
        lock (_gate)
        {
            if (_closed) return;
            _closed = true; _ready = false; ResetSources(clearWindow: true); loop = _loop;
        }
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_coordinator is not null) await _coordinator.StopAsync().ConfigureAwait(false);
        if (loop is not null) await loop.ConfigureAwait(false);
        await _sampling.WaitAsync().ConfigureAwait(false);
        try { if (_windows is IDisposable disposable) disposable.Dispose(); }
        finally { _sampling.Release(); }
    }
}
