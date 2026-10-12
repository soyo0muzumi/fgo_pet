using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FgoPet.Extensibility;

namespace FgoPet.Plugin.DesktopContext;

public sealed record OpenApplication(string Name, int WindowCount, bool IsForeground);
public sealed record ApplicationSnapshot(IReadOnlyList<OpenApplication> Applications, bool Truncated,
    bool ForegroundObserved, DateTimeOffset StartedAtUtc, DateTimeOffset CapturedAtUtc);
public sealed record ContextDiagnostic(DateTimeOffset Timestamp, string Correlation, string Stage, string? ErrorCode);

/// <summary>On-demand observations only. Application access requires a host-owned, revocable grant and reader.</summary>
public sealed class DesktopContextPlugin : IFgoPetPlugin, IDisposable
{
    private readonly Func<ToolScope, bool> _scopeCurrent;
    private readonly TimeProvider _time;
    private readonly Func<ToolScope, bool>? _applicationsAuthorized;
    private readonly Func<CancellationToken, ApplicationSnapshot>? _readApplications;
    private readonly Action<ContextDiagnostic>? _diagnostics;
    private CancellationToken _stopping;
    private bool _started;
    private bool _disposed;
    public PluginManifest Manifest { get; } = new("desktop-context", "1.0.0", 1, []);
    public PluginContributions Contributions { get; }

    public DesktopContextPlugin(Func<ToolScope, bool> scopeCurrent, TimeProvider? time = null,
        Func<ToolScope, bool>? applicationsAuthorized = null,
        Func<CancellationToken, ApplicationSnapshot>? readApplications = null,
        Action<ContextDiagnostic>? diagnostics = null)
    {
        _scopeCurrent = scopeCurrent; _time = time ?? TimeProvider.System;
        _applicationsAuthorized = applicationsAuthorized; _readApplications = readApplications; _diagnostics = diagnostics;
        var tools = System.Collections.Immutable.ImmutableArray.Create<IToolProvider>(new Tool(this, "context.time",
            "Sample this computer's current clock and local time zone. Use the returned timestamp when answering current-time questions; do not infer time from old messages."));
        if (applicationsAuthorized is not null && readApplications is not null)
            tools = tools.Add(new Tool(this, "context.apps",
                "Observe applications with visible top-level Windows windows and the sampled foreground app, only with a host grant. Names are executable stems, not window titles. This does not read window content or list all running processes. Observations can be partial and become stale immediately."));
        Contributions = PluginContributions.Empty with { Tools = tools };
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
