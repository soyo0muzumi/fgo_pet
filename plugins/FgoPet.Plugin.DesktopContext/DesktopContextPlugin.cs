using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using FgoPet.Extensibility;

namespace FgoPet.Plugin.DesktopContext;

public sealed record OpenApplication(string Name, int WindowCount, bool IsForeground);
public sealed record ApplicationSnapshot(IReadOnlyList<OpenApplication> Applications, bool Truncated,
    bool ForegroundObserved, DateTimeOffset StartedAtUtc, DateTimeOffset CapturedAtUtc);
public sealed record ContextDiagnostic(DateTimeOffset Timestamp, string Correlation, string Stage, string? ErrorCode);

/// <summary>On-demand observations only. Application access requires a host-owned, revocable grant and reader.</summary>
public sealed class DesktopContextPlugin : IFgoPetPlugin, IConversationPromptProvider, IAgentContextProvider, IDisposable
{
    private const int MaxSnapshotObservations = 32;
    private const int MaxProjectedObservations = 8;
    private const int MaxObservationChars = 6_000;
    private const int MaxBackgroundChars = 8_000;
    private static readonly JsonSerializerOptions ContextJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly Func<ToolScope, bool> _scopeCurrent;
    private readonly TimeProvider _time;
    private readonly Func<ToolScope, bool>? _applicationsAuthorized;
    private readonly Func<CancellationToken, ApplicationSnapshot>? _readApplications;
    private readonly Action<ContextDiagnostic>? _diagnostics;
    private readonly Func<ToolScope, CompanionContextSnapshot?>? _contextSnapshot;
    private CancellationToken _stopping;
    private bool _started;
    private bool _disposed;
    public PluginManifest Manifest { get; } = new("desktop-context", "1.0.0", 1, []);
    public PluginContributions Contributions { get; }
    public string Id => "ambient-context";
    public bool ProvidesTrustedInstructions => false;

    public DesktopContextPlugin(Func<ToolScope, bool> scopeCurrent, TimeProvider? time = null,
        Func<ToolScope, bool>? applicationsAuthorized = null,
        Func<CancellationToken, ApplicationSnapshot>? readApplications = null,
        Action<ContextDiagnostic>? diagnostics = null,
        Func<ToolScope, CompanionContextSnapshot?>? contextSnapshot = null)
    {
        _scopeCurrent = scopeCurrent; _time = time ?? TimeProvider.System;
        _applicationsAuthorized = applicationsAuthorized; _readApplications = readApplications; _diagnostics = diagnostics;
        _contextSnapshot = contextSnapshot;
        var tools = System.Collections.Immutable.ImmutableArray.Create<IToolProvider>(new Tool(this, "context.time",
            "Sample this computer's current clock and local time zone. Use the returned timestamp when answering current-time questions; do not infer time from old messages."));
        if (applicationsAuthorized is not null && readApplications is not null)
            tools = tools.Add(new Tool(this, "context.apps",
                "Observe applications with visible top-level Windows windows and the sampled foreground app, only with a host grant. Names are executable stems, not window titles. This does not read window content or list all running processes. Observations can be partial and become stale immediately."));
        Contributions = PluginContributions.Empty with { Tools = tools, Prompts = [this], AgentContexts = [this] };
    }

    public ValueTask StartAsync(CancellationToken stoppingToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        stoppingToken.ThrowIfCancellationRequested();
        _stopping = stoppingToken; _started = true;
        return ValueTask.CompletedTask;
    }
    public ValueTask StopAsync(CancellationToken cancellationToken) { _started = false; return ValueTask.CompletedTask; }
    public void Dispose() { _started = false; _disposed = true; }
    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }

    public IReadOnlyList<ConversationPromptBlock> BuildPrompt(ToolScope scope, string userMessage, bool toolsAvailable)
        => BuildContext(scope, default);

    public ValueTask<IReadOnlyList<AgentContextBlock>> BuildAsync(AgentContextRequest request, CancellationToken token)
    {
        var blocks = BuildContext(request.Scope, token).Select(block => new AgentContextBlock(block.Source,
            AgentContextKind.Data, block.Text, block.Source == "current-clock" ? 100 : 60, Whole: true)).ToArray();
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyList<AgentContextBlock>>(ContextAllowed(request.Scope) ? blocks : []);
    }

    private IReadOnlyList<ConversationPromptBlock> BuildContext(ToolScope scope, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!ContextAllowed(scope)) return [];
        var correlation = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("N"))))[..16];
        LogContext("context_received", correlation);
        var initial = ReadSnapshot(scope, correlation);
        token.ThrowIfCancellationRequested();
        if (!ContextAllowed(scope)) return [];
        // The owner supplies cached, bounded facts. A second read catches revoked permissions or
        // changed source generations without invoking Windows discovery from prompt assembly.
        var current = initial is null ? null : ReadSnapshot(scope, correlation);
        token.ThrowIfCancellationRequested();
        if (!ContextAllowed(scope)) return [];
        var sampled = _time.GetUtcNow();
        var zone = _time.LocalTimeZone;
        var blocks = new List<ConversationPromptBlock>
        {
            new("current-clock", JsonSerializer.Serialize(new { source = "system_clock", sampledAtUtc = sampled,
                localTime = TimeZoneInfo.ConvertTime(sampled, zone), timeZoneId = zone.Id,
                utcOffsetMinutes = zone.GetUtcOffset(sampled).TotalMinutes,
                isDaylightSavingTime = zone.IsDaylightSavingTime(sampled) }, ContextJson))
        };
        if (initial is not null && current is not null && initial.Revision == current.Revision)
        {
            var first = UniqueObservations(initial.Observations);
            var latest = UniqueObservations(current.Observations);
            var characters = 0;
            foreach (var observation in current.Observations)
            {
                token.ThrowIfCancellationRequested();
                if (blocks.Count > MaxProjectedObservations) break;
                if (observation is null || !ValidIdentifier(observation.SourceId) || !latest.ContainsKey(observation.SourceId)
                    || !first.TryGetValue(observation.SourceId, out var original)
                    || original.Version != observation.Version || original.Content != observation.Content
                    || original.IsInterpretation != observation.IsInterpretation
                    || !Fresh(original, sampled) || !Fresh(observation, sampled)) continue;
                var content = JsonSerializer.Serialize(new { sourceId = observation.SourceId,
                    sourceVersion = observation.Version, contextRevision = current.Revision,
                    observedAtUtc = observation.ObservedAtUtc.ToUniversalTime(), validUntilUtc = observation.ValidUntilUtc.ToUniversalTime(),
                    content = observation.Content, isInterpretation = observation.IsInterpretation,
                    knowledgeStatus = observation.IsInterpretation ? "tentative_interpretation" : "source_observation" }, ContextJson);
                if (content.Length > MaxBackgroundChars - characters) continue;
                var source = "observation-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(observation.SourceId)))[..24];
                blocks.Add(new(source, content));
                characters += content.Length;
            }
        }
        token.ThrowIfCancellationRequested();
        if (!ContextAllowed(scope)) return [];
        LogContext("context_completed", correlation);
        return blocks;
    }

    private CompanionContextSnapshot? ReadSnapshot(ToolScope scope, string correlation)
    {
        if (_contextSnapshot is null) return null;
        try
        {
            var snapshot = _contextSnapshot(scope);
            if (snapshot is null || snapshot.Scope != scope || !ValidIdentifier(snapshot.Revision) || snapshot.Observations is null)
                return null;
            // Copy the owner's list so a later mutation cannot alter the first sampled generation.
            return snapshot with { Observations = snapshot.Observations.Take(MaxSnapshotObservations).ToArray() };
        }
        catch (Exception)
        {
            LogContext("context_snapshot_failed", correlation, "CONTEXT_SNAPSHOT_UNAVAILABLE");
            return null;
        }
    }

    private static Dictionary<string, CompanionObservation> UniqueObservations(IReadOnlyList<CompanionObservation> observations)
    {
        var result = new Dictionary<string, CompanionObservation>(StringComparer.Ordinal);
        var duplicates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var observation in observations)
        {
            if (observation is null || !ValidIdentifier(observation.SourceId) || !ValidIdentifier(observation.Version)
                || string.IsNullOrWhiteSpace(observation.Content) || observation.Content.Length > MaxObservationChars) continue;
            if (!result.TryAdd(observation.SourceId, observation)) duplicates.Add(observation.SourceId);
        }
        foreach (var duplicate in duplicates) result.Remove(duplicate);
        return result;
    }

    private static bool Fresh(CompanionObservation observation, DateTimeOffset sampled) =>
        observation.ObservedAtUtc <= sampled && observation.ValidUntilUtc > sampled
        && observation.ValidUntilUtc > observation.ObservedAtUtc;

    private static bool ValidIdentifier(string? value) => !string.IsNullOrWhiteSpace(value)
        && value.Length <= 128 && !value.Any(char.IsControl);

    private bool ContextAllowed(ToolScope scope)
    {
        if (!_started || _disposed || _stopping.IsCancellationRequested || scope is null) return false;
        try { return _scopeCurrent(scope); }
        catch (Exception) { return false; }
    }

    private void LogContext(string stage, string correlation, string? error = null)
    {
        try { _diagnostics?.Invoke(new(DateTimeOffset.UtcNow, correlation, stage, error)); }
        catch (Exception) { /* Diagnostics cannot affect context availability or expose content. */ }
    }

    private ValueTask<ToolResult> Invoke(string name, ToolInvocation invocation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var correlation = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            invocation.ExecutionContext?.IdempotencyKey ?? Guid.NewGuid().ToString("N"))))[..16];
        Log("received");
        if (!_started || _disposed || _stopping.IsCancellationRequested) return Fail("CONTEXT_STOPPED");
        if (!_scopeCurrent(invocation.Scope)) return Fail("CONTEXT_SCOPE_DENIED");
        if (invocation.Arguments.ValueKind != JsonValueKind.Object || invocation.Arguments.EnumerateObject().Any())
            return Fail("TOOL_INVALID_ARGUMENTS");
        Log("dispatch");
        JsonElement payload;
        if (name == "context.time")
        {
            var sampled = _time.GetUtcNow();
            var zone = _time.LocalTimeZone;
            payload = JsonSerializer.SerializeToElement(new { source = "system_clock", sampledAtUtc = sampled,
                localTime = TimeZoneInfo.ConvertTime(sampled, zone), timeZoneId = zone.Id,
                utcOffsetMinutes = zone.GetUtcOffset(sampled).TotalMinutes, isDaylightSavingTime = zone.IsDaylightSavingTime(sampled) });
        }
        else
        {
            if (_applicationsAuthorized?.Invoke(invocation.Scope) != true || _readApplications is null)
                return Fail("CONTEXT_APPLICATIONS_NOT_AUTHORIZED");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _stopping);
            var observation = _readApplications(linked.Token);
            if (_applicationsAuthorized(invocation.Scope) != true) return Fail("CONTEXT_AUTHORIZATION_CHANGED");
            payload = JsonSerializer.SerializeToElement(new { source = "visible_top_level_windows",
                observation.StartedAtUtc, observation.CapturedAtUtc,
                applications = observation.Applications.Take(32).Select(app => new {
                    name = new string(app.Name.Where(c => !char.IsControl(c)).Take(128).ToArray()),
                    windowCount = Math.Clamp(app.WindowCount, 1, 512), isForeground = app.IsForeground }),
                truncated = observation.Truncated || observation.Applications.Count > 32,
                foregroundObserved = observation.ForegroundObserved, contentAccess = "not_read" });
        }
        token.ThrowIfCancellationRequested();
        if (!_started || _stopping.IsCancellationRequested) return Fail("CONTEXT_STOPPED");
        if (!_scopeCurrent(invocation.Scope)) return Fail("CONTEXT_SCOPE_DENIED");
        Log("completed");
        return ValueTask.FromResult(new ToolResult(true, payload));

        ValueTask<ToolResult> Fail(string error) { Log("failed", error); return ValueTask.FromResult(new ToolResult(false, JsonSerializer.SerializeToElement(new { }), error)); }
        void Log(string stage, string? error = null) => _diagnostics?.Invoke(new(DateTimeOffset.UtcNow, correlation, stage, error));
    }

    private sealed class Tool(DesktopContextPlugin owner, string name, string description) : IToolProvider
    {
        public ToolDescriptor Descriptor { get; } = new(name, description,
            """{"type":"object","properties":{},"additionalProperties":false}""", ToolEffect.ReadOnly);
        public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken) => owner.Invoke(name, invocation, cancellationToken);
    }
}
