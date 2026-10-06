using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using FgoPet.App.Focus;
using FgoPet.Core.Focus;
using FgoPet.Extensibility;

namespace FgoPet.Plugin.Focus;

/// <summary>Native Focus adapters over the existing Focus session owner and timer.</summary>
public static class NativeFocusTools
{
    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private const int MaxArgumentsBytes = 4096;

    public static IReadOnlyList<IToolProvider> Create(IFocusSessionService owner,
        IFocusNativeDispatcher dispatcher, Func<string?> activeRole)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(activeRole);

        var access = new FocusAccess(owner, dispatcher, activeRole);
        var receipts = new CommandReceipts();
        return Array.AsReadOnly<IToolProvider>([
            new Reader(access),
            new Command("focus.start", access, receipts),
            new Command("focus.pause", access, receipts),
            new Command("focus.stop", access, receipts),
        ]);
    }

    private sealed class FocusAccess(IFocusSessionService owner, IFocusNativeDispatcher dispatcher,
        Func<string?> activeRole)
    {
        public ToolResourceAuthorization GetAuthorization(ToolScope scope)
        {
            try
            {
                return dispatcher.Invoke(() => Bind(scope, owner.Current));
            }
            catch (WorkspaceAccessException) { throw; }
            catch (Exception) { throw new WorkspaceAccessException("FOCUS_UNAVAILABLE"); }
        }

        public async ValueTask<bool> ValidateRoleAsync(ToolScope scope, CancellationToken token)
        {
            try
            {
                return await dispatcher.InvokeAsync(() =>
                {
                    token.ThrowIfCancellationRequested();
                    ValidateRole(scope);
                    return true;
                }, token);
            }
            catch (WorkspaceAccessException) { throw; }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { throw new WorkspaceAccessException("FOCUS_UNAVAILABLE"); }
        }

        public ValueTask<ToolResult> ReadAsync(ToolInvocation invocation, CancellationToken token)
        {
            if (!HasHostResource(invocation)) return ValueTask.FromResult(Failure("FOCUS_SCOPE_DENIED"));
            if (!HasOnlyProperties(invocation.Arguments)) return ValueTask.FromResult(Failure("TOOL_INVALID_ARGUMENTS"));

            return ReadOnOwnerAsync(invocation, token);
        }

        private async ValueTask<ToolResult> ReadOnOwnerAsync(ToolInvocation invocation, CancellationToken token)
        {
            try
            {
                var result = await dispatcher.InvokeAsync(() =>
                {
                    token.ThrowIfCancellationRequested();
                    var session = owner.Current;
                    var currentAuthorization = Bind(invocation.Scope, session);
                    if (currentAuthorization != invocation.ExecutionContext!.ResourceAuthorization)
                        return Failure("FOCUS_STATE_CHANGED");
                    return new ToolResult(true, JsonSerializer.SerializeToElement(Snapshot(session), Json));
                }, token);
                token.ThrowIfCancellationRequested();
                return result;
            }
            catch (WorkspaceAccessException error) { return Failure(error.Code); }
            catch (OperationCanceledException) { return Failure("TOOL_CANCELLED"); }
            catch (Exception) { return Failure("FOCUS_UNAVAILABLE"); }
        }

        public ToolResult ExecuteOnOwner(ToolInvocation invocation, string command, Action markAttempted)
        {
            var session = owner.Current;
            var currentAuthorization = Bind(invocation.Scope, session);
            if (currentAuthorization != invocation.ExecutionContext!.ResourceAuthorization)
                return Failure("FOCUS_STATE_CHANGED");

            if (command == "focus.start")
            {
                if (!TryReadStart(invocation.Arguments, out var focusMinutes, out var breakMinutes, out var cycles))
                    return Failure("TOOL_INVALID_ARGUMENTS");
                FocusPreset preset;
                try { preset = FocusPreset.Create(focusMinutes, breakMinutes, cycles); }
                catch (ArgumentOutOfRangeException) { return Failure("TOOL_INVALID_ARGUMENTS"); }
                if (session.Status is not (FocusStatus.Idle or FocusStatus.Completed))
                    return Failure("FOCUS_STATE_CONFLICT");

                var role = ValidateRole(invocation.Scope);
                return Apply("focus.start", session, role, preset, markAttempted);
            }

            if (!TryReadSessionId(invocation.Arguments, out var expectedSessionId))
                return Failure("TOOL_INVALID_ARGUMENTS");
            if (!string.Equals(session.SessionId, expectedSessionId, StringComparison.Ordinal)
                || command == "focus.pause" && session.Status is not (FocusStatus.Focusing or FocusStatus.Breaking)
                || command == "focus.stop" && session.Status is not (FocusStatus.Focusing or FocusStatus.Breaking
                    or FocusStatus.PausedFocus or FocusStatus.PausedBreak))
                return Failure("FOCUS_STATE_CONFLICT");

            return Apply(command, session, session.ServantId, preset: null, markAttempted);
        }

        public async ValueTask<ToolResult> ExecuteCommandAsync(ToolInvocation invocation, string command,
            CancellationToken token)
        {
            var attempted = false;
            try
            {
                return await dispatcher.InvokeAsync(() =>
                {
                    token.ThrowIfCancellationRequested();
                    return ExecuteOnOwner(invocation, command, () => attempted = true);
                }, token);
            }
            catch (WorkspaceAccessException error) { return Failure(error.Code); }
            catch (OperationCanceledException) { return attempted ? Unknown() : Failure("TOOL_CANCELLED"); }
            catch (Exception) { return attempted ? Unknown() : Failure("FOCUS_UNAVAILABLE"); }
        }

        private ToolResult Apply(string command, FocusSession before, string servantId, FocusPreset? preset,
            Action markAttempted)
        {
            markAttempted();
            var persistenceFailed = false;
            EventHandler failureHandler = (_, _) => persistenceFailed = true;
            owner.PersistenceFailed += failureHandler;
            try
            {
                switch (command)
                {
                    case "focus.start": owner.Start(preset!, servantId); break;
                    case "focus.pause": owner.Pause(); break;
                    case "focus.stop": owner.Stop(); break;
                    default: return Failure("FOCUS_TOOL_UNAVAILABLE");
                }
            }
            finally
            {
                owner.PersistenceFailed -= failureHandler;
            }

            var after = owner.Current;
            if (persistenceFailed) return Unknown();
            if (after == before) return Failure("FOCUS_COMMAND_NOT_APPLIED");

            var expected = command switch
            {
                "focus.start" => after.Status == FocusStatus.Focusing && after.IsCurrent
                    && after.ServantId == servantId && after.FocusSeconds == preset!.FocusSeconds
                    && after.BreakSeconds == preset.BreakSeconds && after.TotalCycles == preset.Cycles
                    && !string.IsNullOrEmpty(after.SessionId),
                "focus.pause" => after.SessionId == before.SessionId && after.IsCurrent
                    && (before.Status == FocusStatus.Focusing && after.Status == FocusStatus.PausedFocus
                        || before.Status == FocusStatus.Breaking && after.Status == FocusStatus.PausedBreak),
                "focus.stop" => after.SessionId == before.SessionId && after.Status == FocusStatus.Idle && !after.IsCurrent,
                _ => false,
            };
            if (!expected) return Unknown();

            var payload = Snapshot(after);
            if (command == "focus.stop")
                payload = payload with { AppliedToSessionId = before.SessionId };
            return new ToolResult(true, JsonSerializer.SerializeToElement(payload, Json))
            {
                ExecutionState = ToolExecutionState.Committed,
            };
        }

        private ToolResourceAuthorization Bind(ToolScope scope, FocusSession session)
        {
            var role = ValidateRole(scope);
            if (session is null) throw new WorkspaceAccessException("FOCUS_UNAVAILABLE");
            var resourceId = string.IsNullOrEmpty(session.SessionId) ? "focus-idle" : $"focus-{session.SessionId}";
            var fingerprintData = new FocusBinding(session.SessionId, FocusStatusKeys.Key(session.Status),
                FocusPhaseKeys.Key(session.Phase), session.CurrentCycle, session.TotalCycles,
                session.FocusSeconds, session.BreakSeconds, role);
            var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(fingerprintData, Json)));
            return new ToolResourceAuthorization(resourceId, 0) { Fingerprint = fingerprint };
        }

        private string ValidateRole(ToolScope scope)
        {
            if (scope is null || string.IsNullOrWhiteSpace(scope.RoleId))
                throw new WorkspaceAccessException("FOCUS_SCOPE_DENIED");
            string? role;
            try { role = activeRole(); }
            catch (Exception) { throw new WorkspaceAccessException("FOCUS_UNAVAILABLE"); }
            if (string.IsNullOrWhiteSpace(role) || !string.Equals(scope.RoleId, role, StringComparison.Ordinal))
                throw new WorkspaceAccessException("FOCUS_SCOPE_DENIED");
            return role;
        }
    }

    private sealed class Reader(FocusAccess access) : IToolProvider, IToolResourceAuthorizationProvider
    {
        public ToolDescriptor Descriptor { get; } = new("focus.get",
            "Read the current Focus session for the active role.",
            """{"type":"object","properties":{},"additionalProperties":false}""", ToolEffect.ReadOnly);

        public ToolResourceAuthorization GetAuthorization(ToolScope scope) => access.GetAuthorization(scope);

        public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken) =>
            access.ReadAsync(invocation, cancellationToken);
    }

    private sealed class Command(string name, FocusAccess access, CommandReceipts receipts)
        : IToolProvider, IToolResourceAuthorizationProvider
    {
        private readonly string _name = name;

        public ToolDescriptor Descriptor { get; } = new(name, name switch
        {
            "focus.start" => "Start a bounded Focus session for the active role.",
            "focus.pause" => "Pause the current Focus session.",
            _ => "Stop the current Focus session.",
        }, name == "focus.start"
            ? """{"type":"object","properties":{"focusMinutes":{"type":"integer","minimum":5,"maximum":180},"breakMinutes":{"type":"integer","minimum":1,"maximum":60},"cycles":{"type":"integer","minimum":1,"maximum":12}},"required":["focusMinutes","breakMinutes","cycles"],"additionalProperties":false}"""
            : """{"type":"object","properties":{"expectedSessionId":{"type":"string","minLength":1,"maxLength":128}},"required":["expectedSessionId"],"additionalProperties":false}""",
            ToolEffect.Command);

        public ToolResourceAuthorization GetAuthorization(ToolScope scope) => access.GetAuthorization(scope);

        public async ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken)
        {
            if (invocation is null || invocation.ExecutionContext is not { } execution
                || string.IsNullOrWhiteSpace(execution.RunId) || string.IsNullOrWhiteSpace(execution.CallId)
                || string.IsNullOrWhiteSpace(execution.IdempotencyKey) || execution.IdempotencyKey.Length > 128)
                return Failure("TOOL_EXECUTION_CONTEXT_REQUIRED");
            if (Encoding.UTF8.GetByteCount(invocation.Arguments.GetRawText()) > MaxArgumentsBytes)
                return Failure("TOOL_INVALID_ARGUMENTS");

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await access.ValidateRoleAsync(invocation.Scope, cancellationToken);
                var fingerprint = ArgumentsFingerprint(_name, invocation);
                return await receipts.ExecuteAsync(execution.IdempotencyKey, fingerprint,
                    () => ExecuteAsync(invocation, cancellationToken), cancellationToken);
            }
            catch (WorkspaceAccessException error) { return Failure(error.Code); }
            catch (OperationCanceledException) { return Failure("TOOL_CANCELLED"); }
            catch (Exception) { return Failure("FOCUS_UNAVAILABLE"); }
        }

        private async ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken token)
        {
            return await access.ExecuteCommandAsync(invocation, _name, token);
        }
    }

    private sealed class CommandReceipts
    {
        private const int Capacity = 64;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly Dictionary<string, Receipt> _receipts = new(StringComparer.Ordinal);
        private readonly Queue<string> _insertionOrder = new();

        public async ValueTask<ToolResult> ExecuteAsync(string key, string fingerprint,
            Func<ValueTask<ToolResult>> execute, CancellationToken token)
        {
            await _gate.WaitAsync(token);
            try
            {
                if (_receipts.TryGetValue(key, out var existing))
                    return existing.Fingerprint == fingerprint ? existing.Result : Failure("FOCUS_IDEMPOTENCY_CONFLICT");

                var result = await execute();
                _receipts.Add(key, new Receipt(fingerprint, result));
                _insertionOrder.Enqueue(key);
                while (_receipts.Count > Capacity)
                {
                    var oldest = _insertionOrder.Dequeue();
                    _receipts.Remove(oldest);
                }
                return result;
            }
            finally { _gate.Release(); }
        }

        private sealed record Receipt(string Fingerprint, ToolResult Result);
    }

    private sealed record FocusBinding(string SessionId, string Status, string Phase, int CurrentCycle,
        int TotalCycles, int FocusSeconds, int BreakSeconds, string ActiveRole);

    private sealed record FocusPayload(string SessionId, string Status, string Phase, int FocusSeconds,
        int BreakSeconds, int TotalCycles, int CurrentCycle, int RemainingSeconds, int PhaseElapsedSeconds,
        string ServantId, DateTimeOffset StartedAtUtc, DateTimeOffset UpdatedAtUtc, bool IsCurrent,
        int DurationSeconds, string? AppliedToSessionId = null);

    private static FocusPayload Snapshot(FocusSession session) => new(session.SessionId,
        FocusStatusKeys.Key(session.Status), FocusPhaseKeys.Key(session.Phase), session.FocusSeconds,
        session.BreakSeconds, session.TotalCycles, session.CurrentCycle, session.RemainingSeconds,
        session.PhaseElapsedSeconds, session.ServantId, session.StartedAtUtc, session.UpdatedAtUtc,
        session.IsCurrent, DurationSeconds(session));

    private static int DurationSeconds(FocusSession session) => session.FocusSeconds * session.TotalCycles
        + session.BreakSeconds * Math.Max(0, session.TotalCycles - 1);

    private static bool HasHostResource(ToolInvocation invocation) =>
        invocation?.ExecutionContext?.ResourceAuthorization is not null;

    private static bool TryReadStart(JsonElement arguments, out int focusMinutes, out int breakMinutes, out int cycles)
    {
        focusMinutes = breakMinutes = cycles = 0;
        if (!HasOnlyProperties(arguments, "focusMinutes", "breakMinutes", "cycles")
            || !arguments.TryGetProperty("focusMinutes", out var focus)
            || !arguments.TryGetProperty("breakMinutes", out var pause)
            || !arguments.TryGetProperty("cycles", out var cycleCount)
            || focus.ValueKind != JsonValueKind.Number || !focus.TryGetInt32(out focusMinutes)
            || pause.ValueKind != JsonValueKind.Number || !pause.TryGetInt32(out breakMinutes)
            || cycleCount.ValueKind != JsonValueKind.Number || !cycleCount.TryGetInt32(out cycles))
            return false;
        return true;
    }

    private static bool TryReadSessionId(JsonElement arguments, out string expectedSessionId)
    {
        expectedSessionId = string.Empty;
        return HasOnlyProperties(arguments, "expectedSessionId")
            && arguments.TryGetProperty("expectedSessionId", out var session)
            && session.ValueKind == JsonValueKind.String
            && (expectedSessionId = session.GetString() ?? string.Empty) is { Length: > 0 and <= 128 };
    }

    private static bool HasOnlyProperties(JsonElement arguments, params string[] names)
    {
        if (arguments.ValueKind != JsonValueKind.Object) return false;
        var expected = new HashSet<string>(names, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in arguments.EnumerateObject())
            if (!expected.Contains(property.Name) || !seen.Add(property.Name)) return false;
        return seen.Count == expected.Count;
    }

    private static string ArgumentsFingerprint(string name, ToolInvocation invocation)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            tool = name,
            scope = invocation.Scope,
            arguments = invocation.Arguments.GetRawText(),
        }, Json);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static ToolResult Failure(string code) => new(false, JsonSerializer.SerializeToElement(new { }), code)
    {
        ExecutionState = ToolExecutionState.NotExecuted,
    };

    private static ToolResult Unknown() => new(false, JsonSerializer.SerializeToElement(new { }), "FOCUS_EXECUTION_UNKNOWN")
    {
        ExecutionState = ToolExecutionState.Unknown,
    };
}
