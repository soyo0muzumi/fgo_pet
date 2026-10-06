using System.Text.Json;
using FgoPet.App.Focus;
using FgoPet.Core.Focus;
using FgoPet.Extensibility;
using FgoPet.Plugin.Focus;
using Xunit;

namespace FgoPet.Capability.Tests.Focus;

public sealed class NativeFocusToolsTests
{
    [Fact]
    public async Task Get_reads_the_owner_on_its_dispatcher_and_rejects_a_role_scope_mismatch()
    {
        var owner = new FakeFocusSessionService();
        var dispatcher = new InlineFocusDispatcher();
        var tools = Create(owner, dispatcher, "role-a");
        var get = Tool(tools, "focus.get");

        var result = await Invoke(get, Scope(), new { }, "get-1");

        Assert.True(result.Success);
        Assert.Equal(ToolEffect.ReadOnly, get.Descriptor.Effect);
        Assert.Equal("idle", result.Payload.GetProperty("status").GetString());
        Assert.Equal(1, dispatcher.SyncCalls); // resource binding
        Assert.Equal(1, dispatcher.AsyncCalls); // snapshot read

        var currentAuthorization = ((IToolResourceAuthorizationProvider)get).GetAuthorization(Scope());
        var denied = await Invoke(get, Scope("role-b"), new { }, "get-2", currentAuthorization);
        Assert.False(denied.Success);
        Assert.Equal("FOCUS_SCOPE_DENIED", denied.ErrorCode);
        Assert.Equal(ToolExecutionState.NotExecuted, denied.ExecutionState);
        var scopeException = Assert.Throws<WorkspaceAccessException>(() =>
            ((IToolResourceAuthorizationProvider)get).GetAuthorization(Scope("role-b")));
        Assert.Equal("FOCUS_SCOPE_DENIED", scopeException.Code);
    }

    [Fact]
    public void Resource_fingerprint_ignores_countdown_and_timestamp_projection_changes()
    {
        var owner = new FakeFocusSessionService();
        owner.Start(FocusPreset.Create(25, 5, 4), "role-a");
        var get = Tool(Create(owner, new InlineFocusDispatcher(), "role-a"), "focus.get");
        var first = ((IToolResourceAuthorizationProvider)get).GetAuthorization(Scope());

        owner.AdvanceDisplayOnly();
        var second = ((IToolResourceAuthorizationProvider)get).GetAuthorization(Scope());

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task Command_rejects_a_frozen_focus_state_that_changed_before_execution()
    {
        var owner = new FakeFocusSessionService();
        var tools = Create(owner, new InlineFocusDispatcher(), "role-a");
        var pause = Tool(tools, "focus.pause");
        var authorization = ((IToolResourceAuthorizationProvider)pause).GetAuthorization(Scope());
        owner.Start(FocusPreset.Create(25, 5, 4), "role-a");

        var result = await Invoke(pause, Scope(), new { expectedSessionId = owner.Current.SessionId }, "pause-stale", authorization);

        Assert.False(result.Success);
        Assert.Equal("FOCUS_STATE_CHANGED", result.ErrorCode);
        Assert.Equal(ToolExecutionState.NotExecuted, result.ExecutionState);
        Assert.Equal(0, owner.PauseCalls);
    }

    [Fact]
    public async Task Start_pause_and_stop_apply_once_and_replay_the_same_idempotency_receipt()
    {
        var owner = new FakeFocusSessionService();
        var tools = Create(owner, new InlineFocusDispatcher(), "role-a");
        var start = Tool(tools, "focus.start");
        var startArgs = new { focusMinutes = 25, breakMinutes = 5, cycles = 4 };

        var started = await Invoke(start, Scope(), startArgs, "start-1");
        var repeatedStart = await Invoke(start, Scope(), startArgs, "start-1");
        var sessionId = started.Payload.GetProperty("sessionId").GetString();

        Assert.True(started.Success);
        Assert.Equal(ToolEffect.Command, start.Descriptor.Effect);
        Assert.False(string.IsNullOrWhiteSpace(sessionId));
        Assert.Equal("focusing", started.Payload.GetProperty("status").GetString());
        Assert.Equal("role-a", started.Payload.GetProperty("servantId").GetString());
        Assert.Equal(6900, started.Payload.GetProperty("durationSeconds").GetInt32());
        Assert.Equal(started.Payload.GetRawText(), repeatedStart.Payload.GetRawText());
        Assert.Equal(1, owner.StartCalls);

        var pause = Tool(tools, "focus.pause");
        var pauseArgs = new { expectedSessionId = sessionId };
        var paused = await Invoke(pause, Scope(), pauseArgs, "pause-1");
        var repeatedPause = await Invoke(pause, Scope(), pauseArgs, "pause-1");
        Assert.True(paused.Success);
        Assert.Equal("paused_focus", paused.Payload.GetProperty("status").GetString());
        Assert.Equal(paused.Payload.GetRawText(), repeatedPause.Payload.GetRawText());
        Assert.Equal(1, owner.PauseCalls);

        var stop = Tool(tools, "focus.stop");
        var stopArgs = new { expectedSessionId = sessionId };
        var stopped = await Invoke(stop, Scope(), stopArgs, "stop-1");
        var repeatedStop = await Invoke(stop, Scope(), stopArgs, "stop-1");
        Assert.True(stopped.Success);
        Assert.Equal("idle", stopped.Payload.GetProperty("status").GetString());
        Assert.Equal(sessionId, stopped.Payload.GetProperty("appliedToSessionId").GetString());
        Assert.Equal(stopped.Payload.GetRawText(), repeatedStop.Payload.GetRawText());
        Assert.Equal(1, owner.StopCalls);
        Assert.Equal(FocusStatus.Idle, owner.Current.Status);
    }

    [Fact]
    public async Task Same_idempotency_key_with_different_arguments_is_rejected_without_a_second_start()
    {
        var owner = new FakeFocusSessionService();
        var tools = Create(owner, new InlineFocusDispatcher(), "role-a");
        var start = Tool(tools, "focus.start");

        var first = await Invoke(start, Scope(), new { focusMinutes = 25, breakMinutes = 5, cycles = 4 }, "same-key");
        var changed = await Invoke(start, Scope(), new { focusMinutes = 50, breakMinutes = 10, cycles = 2 }, "same-key");

        Assert.True(first.Success);
        Assert.False(changed.Success);
        Assert.Equal("FOCUS_IDEMPOTENCY_CONFLICT", changed.ErrorCode);
        Assert.Equal(ToolExecutionState.NotExecuted, changed.ExecutionState);
        Assert.Equal(1, owner.StartCalls);
    }

    [Fact]
    public async Task Invalid_state_or_session_conflict_does_not_call_the_owner_command()
    {
        var owner = new FakeFocusSessionService();
        var tools = Create(owner, new InlineFocusDispatcher(), "role-a");
        var pause = Tool(tools, "focus.pause");
        var stop = Tool(tools, "focus.stop");

        var idlePause = await Invoke(pause, Scope(), new { expectedSessionId = "missing" }, "pause-idle");
        Assert.Equal("FOCUS_STATE_CONFLICT", idlePause.ErrorCode);
        Assert.Equal(0, owner.PauseCalls);

        owner.Start(FocusPreset.Create(25, 5, 4), "role-a");
        var wrongSession = await Invoke(stop, Scope(), new { expectedSessionId = "stale-session" }, "stop-wrong-session");
        Assert.Equal("FOCUS_STATE_CONFLICT", wrongSession.ErrorCode);
        Assert.Equal(0, owner.StopCalls);

        var start = Tool(tools, "focus.start");
        var activeStart = await Invoke(start, Scope(), new { focusMinutes = 25, breakMinutes = 5, cycles = 4 }, "start-active");
        Assert.Equal("FOCUS_STATE_CONFLICT", activeStart.ErrorCode);
        Assert.Equal(1, owner.StartCalls);
    }

    [Fact]
    public async Task Persistence_failure_returns_unknown_and_idempotent_retry_does_not_repeat_the_command()
    {
        var owner = new FakeFocusSessionService { FailNextPersistence = true };
        var tools = Create(owner, new InlineFocusDispatcher(), "role-a");
        var start = Tool(tools, "focus.start");
        var arguments = new { focusMinutes = 25, breakMinutes = 5, cycles = 4 };

        var first = await Invoke(start, Scope(), arguments, "uncertain-start");
        var retry = await Invoke(start, Scope(), arguments, "uncertain-start");

        Assert.False(first.Success);
        Assert.Equal("FOCUS_EXECUTION_UNKNOWN", first.ErrorCode);
        Assert.Equal(ToolExecutionState.Unknown, first.ExecutionState);
        Assert.Equal(first.Payload.GetRawText(), retry.Payload.GetRawText());
        Assert.Equal(ToolExecutionState.Unknown, retry.ExecutionState);
        Assert.Equal(1, owner.StartCalls);
        Assert.Equal(FocusStatus.PausedFocus, owner.Current.Status);
    }

    [Fact]
    public async Task Command_without_host_execution_context_cannot_start_focus()
    {
        var owner = new FakeFocusSessionService();
        var start = Tool(Create(owner, new InlineFocusDispatcher(), "role-a"), "focus.start");

        var result = await start.InvokeAsync(new ToolInvocation(Scope(), JsonSerializer.SerializeToElement(
            new { focusMinutes = 25, breakMinutes = 5, cycles = 4 })), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOOL_EXECUTION_CONTEXT_REQUIRED", result.ErrorCode);
        Assert.Equal(ToolExecutionState.NotExecuted, result.ExecutionState);
        Assert.Equal(0, owner.StartCalls);
    }

    private static IReadOnlyList<IToolProvider> Create(FakeFocusSessionService owner,
        InlineFocusDispatcher dispatcher, string? activeRole) => NativeFocusTools.Create(owner, dispatcher, () => activeRole);

    private static IToolProvider Tool(IReadOnlyList<IToolProvider> tools, string name) =>
        Assert.Single(tools, tool => tool.Descriptor.Name == name);

    private static ToolScope Scope(string roleId = "role-a") => new("conversation-1", roleId, "project-1");

    private static async Task<ToolResult> Invoke(IToolProvider tool, ToolScope scope, object arguments,
        string idempotencyKey, ToolResourceAuthorization? authorization = null)
    {
        authorization ??= ((IToolResourceAuthorizationProvider)tool).GetAuthorization(scope);
        var execution = new ToolExecutionContext("run-1", 1, idempotencyKey, idempotencyKey)
        {
            ResourceAuthorization = authorization,
        };
        return await tool.InvokeAsync(new ToolInvocation(scope, JsonSerializer.SerializeToElement(arguments))
        {
            ExecutionContext = execution,
        }, CancellationToken.None);
    }

    private sealed class InlineFocusDispatcher : IFocusNativeDispatcher
    {
        public int SyncCalls { get; private set; }
        public int AsyncCalls { get; private set; }

        public T Invoke<T>(Func<T> operation)
        {
            SyncCalls++;
            return operation();
        }

        public ValueTask<T> InvokeAsync<T>(Func<T> operation, CancellationToken cancellationToken)
        {
            AsyncCalls++;
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(operation());
        }
    }

    private sealed class FakeFocusSessionService : IFocusSessionService
    {
        private DateTimeOffset _at = new(2026, 10, 6, 0, 0, 0, TimeSpan.Zero);

        public FocusSession Current { get; private set; } = FocusSession.Idle;
        public int StartCalls { get; private set; }
        public int PauseCalls { get; private set; }
        public int StopCalls { get; private set; }
        public bool FailNextPersistence { get; set; }
        public event EventHandler? SnapshotChanged;
        public event EventHandler? PersistenceFailed;

        public void Start(FocusPreset preset, string servantId)
        {
            StartCalls++;
            var previous = Current.Status is FocusStatus.Idle or FocusStatus.Completed ? FocusSession.Idle : Current;
            Apply(previous, new FocusCommand.Start(preset, servantId));
        }

        public void Pause()
        {
            PauseCalls++;
            Apply(Current, new FocusCommand.Pause());
        }

        public void Resume() => Apply(Current, new FocusCommand.Resume());
        public void Stop()
        {
            StopCalls++;
            Apply(Current, new FocusCommand.Stop());
        }
        public void Tick() { }
        public void Restore() { }

        public void AdvanceDisplayOnly() => Current = Current with
        {
            RemainingSeconds = Math.Max(0, Current.RemainingSeconds - 1),
            UpdatedAtUtc = Current.UpdatedAtUtc.AddSeconds(1),
        };

        private void Apply(FocusSession input, FocusCommand command)
        {
            Current = FocusStateMachine.Apply(input, command, _at).Session;
            _at = _at.AddSeconds(1);
            SnapshotChanged?.Invoke(this, EventArgs.Empty);
            if (!FailNextPersistence) return;
            FailNextPersistence = false;
            Current = Current.RestorePaused();
            PersistenceFailed?.Invoke(this, EventArgs.Empty);
        }
    }
}
