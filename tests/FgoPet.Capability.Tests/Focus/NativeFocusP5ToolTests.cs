using System.Text.Json;
using FgoPet.App.Focus;
using FgoPet.Core.Focus;
using FgoPet.Extensibility;
using FgoPet.Plugin.Focus;
using Xunit;

namespace FgoPet.Capability.Tests.Focus;

public sealed class NativeFocusP5ToolTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Resume_preserves_phase_budget_and_cycle_and_replays_without_a_second_command(bool breakPhase)
    {
        var owner = PausedOwner(breakPhase);
        var before = owner.Current;
        var resume = Tool(Create(owner, new InlineFocusDispatcher(), "role-a"), "focus.resume");
        var arguments = new { expectedSessionId = before.SessionId };

        var result = await Invoke(resume, Scope(), arguments, "resume-1");
        owner.AdvanceDisplayOnly();
        var repeated = await Invoke(resume, Scope(), arguments, "resume-1");

        Assert.True(result.Success);
        Assert.Equal(ToolEffect.Command, resume.Descriptor.Effect);
        Assert.Equal(ToolExecutionState.Committed, result.ExecutionState);
        Assert.Equal(breakPhase ? "breaking" : "focusing", result.Payload.GetProperty("status").GetString());
        Assert.Equal(before.SessionId, result.Payload.GetProperty("sessionId").GetString());
        Assert.Equal(before.RemainingSeconds, result.Payload.GetProperty("remainingSeconds").GetInt32());
        Assert.Equal(before.PhaseElapsedSeconds, result.Payload.GetProperty("phaseElapsedSeconds").GetInt32());
        Assert.Equal(before.CurrentCycle, result.Payload.GetProperty("currentCycle").GetInt32());
        Assert.Equal(before.TotalCycles, result.Payload.GetProperty("totalCycles").GetInt32());
        Assert.Equal(before.Phase, owner.Current.Phase);
        Assert.Equal(result.Payload.GetRawText(), repeated.Payload.GetRawText());
        Assert.Equal(1, owner.ResumeCalls);
        Assert.Equal(1, owner.StartCalls);
    }

    [Fact]
    public async Task Resume_rejects_wrong_session_changed_authorization_and_running_state_without_mutation()
    {
        var owner = PausedOwner(false);
        var resume = Tool(Create(owner, new InlineFocusDispatcher(), "role-a"), "focus.resume");
        var authorization = ((IToolResourceAuthorizationProvider)resume).GetAuthorization(Scope());

        var wrongSession = await Invoke(resume, Scope(), new { expectedSessionId = "stale-session" }, "wrong-session");
        Assert.Equal("FOCUS_STATE_CONFLICT", wrongSession.ErrorCode);
        Assert.Equal(0, owner.ResumeCalls);

        owner.Resume();
        var stale = await Invoke(resume, Scope(), new { expectedSessionId = owner.Current.SessionId }, "stale", authorization);
        var running = await Invoke(resume, Scope(), new { expectedSessionId = owner.Current.SessionId }, "running");
        Assert.Equal("FOCUS_STATE_CHANGED", stale.ErrorCode);
        Assert.Equal("FOCUS_STATE_CONFLICT", running.ErrorCode);
        Assert.Equal(ToolExecutionState.NotExecuted, stale.ExecutionState);
        Assert.Equal(ToolExecutionState.NotExecuted, running.ExecutionState);
        Assert.Equal(1, owner.ResumeCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    public async Task Resume_rejects_authorization_for_an_earlier_pause_even_in_the_same_phase_and_cycle(int elapsedSeconds)
    {
        var owner = PausedOwner(false);
        var resume = Tool(Create(owner, new InlineFocusDispatcher(), "role-a"), "focus.resume");
        var binding = (IToolResourceAuthorizationProvider)resume;
        var authorization = binding.GetAuthorization(Scope());
        Assert.Equal(authorization, binding.GetAuthorization(Scope()));
        owner.Resume();
        if (elapsedSeconds > 0) owner.Advance(elapsedSeconds);
        owner.Pause();
        Assert.NotEqual(authorization, binding.GetAuthorization(Scope()));

        var result = await Invoke(resume, Scope(), new { expectedSessionId = owner.Current.SessionId }, "earlier-pause", authorization);

        Assert.Equal("FOCUS_STATE_CHANGED", result.ErrorCode);
        Assert.Equal(ToolExecutionState.NotExecuted, result.ExecutionState);
        Assert.Equal(FocusStatus.PausedFocus, owner.Current.Status);
        Assert.Equal(1, owner.ResumeCalls);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"expectedSessionId\":42}")]
    [InlineData("{\"expectedSessionId\":\"\"}")]
    [InlineData("{\"expectedSessionId\":\"session\",\"extra\":true}")]
    [InlineData("{\"expectedSessionId\":\"session\",\"expectedSessionId\":\"session\"}")]
    public async Task Resume_rejects_malformed_arguments_without_calling_owner(string json)
    {
        var owner = PausedOwner(false);
        var resume = Tool(Create(owner, new InlineFocusDispatcher(), "role-a"), "focus.resume");
        using var arguments = JsonDocument.Parse(json);

        var result = await Invoke(resume, Scope(), arguments.RootElement, "invalid");

        Assert.Equal("TOOL_INVALID_ARGUMENTS", result.ErrorCode);
        Assert.Equal(ToolExecutionState.NotExecuted, result.ExecutionState);
        Assert.Equal(0, owner.ResumeCalls);
    }

    [Fact]
    public async Task Resume_requires_host_context_and_role_and_honors_pre_execution_cancellation()
    {
        var owner = PausedOwner(false);
        var resume = Tool(Create(owner, new InlineFocusDispatcher(), "role-a"), "focus.resume");
        var args = new { expectedSessionId = owner.Current.SessionId };
        var authorization = ((IToolResourceAuthorizationProvider)resume).GetAuthorization(Scope());

        var noContext = await resume.InvokeAsync(new(Scope(), JsonSerializer.SerializeToElement(args)), default);
        var wrongRole = await Invoke(resume, Scope("role-b"), args, "wrong-role", authorization);
        var cancelled = await Invoke(resume, Scope(), args, "cancelled", cancellationToken: new(true));

        Assert.Equal("TOOL_EXECUTION_CONTEXT_REQUIRED", noContext.ErrorCode);
        Assert.Equal("FOCUS_SCOPE_DENIED", wrongRole.ErrorCode);
        Assert.Equal("TOOL_CANCELLED", cancelled.ErrorCode);
        Assert.Equal(0, owner.ResumeCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Resume_persistence_failure_remains_unknown_and_is_not_retried(bool breakPhase)
    {
        var owner = PausedOwner(breakPhase);
        owner.FailNextPersistence = true;
        var resume = Tool(Create(owner, new InlineFocusDispatcher(), "role-a"), "focus.resume");
        var args = new { expectedSessionId = owner.Current.SessionId };

        var first = await Invoke(resume, Scope(), args, "uncertain-resume");
        var repeated = await Invoke(resume, Scope(), args, "uncertain-resume");

        Assert.False(first.Success);
        Assert.Equal("FOCUS_EXECUTION_UNKNOWN", first.ErrorCode);
        Assert.Equal(ToolExecutionState.Unknown, first.ExecutionState);
        Assert.Equal(first, repeated);
        Assert.Equal(1, owner.ResumeCalls);
        Assert.Equal(breakPhase ? FocusStatus.PausedBreak : FocusStatus.PausedFocus, owner.Current.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Resume_exception_or_cancellation_after_attempt_is_unknown_and_sanitized(bool cancellation)
    {
        var owner = PausedOwner(false);
        owner.ResumeError = cancellation ? new OperationCanceledException("private fixture") : new IOException("private fixture");
        var resume = Tool(Create(owner, new InlineFocusDispatcher(), "role-a"), "focus.resume");
        var args = new { expectedSessionId = owner.Current.SessionId };

        var first = await Invoke(resume, Scope(), args, "throwing-resume");
        var repeated = await Invoke(resume, Scope(), args, "throwing-resume");

        Assert.Equal("FOCUS_EXECUTION_UNKNOWN", first.ErrorCode);
        Assert.Equal(ToolExecutionState.Unknown, first.ExecutionState);
        Assert.Equal("{}", first.Payload.GetRawText());
        Assert.Equal(first, repeated);
        Assert.Equal(1, owner.ResumeCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Resume_never_reports_success_for_a_noop_or_changed_timer_budget(bool changesBudget)
    {
        var owner = PausedOwner(false);
        owner.IgnoreResume = !changesBudget;
        owner.ChangeBudgetOnResume = changesBudget;
        var resume = Tool(Create(owner, new InlineFocusDispatcher(), "role-a"), "focus.resume");

        var result = await Invoke(resume, Scope(), new { expectedSessionId = owner.Current.SessionId }, "bad-owner");

        Assert.False(result.Success);
        Assert.Equal(changesBudget ? "FOCUS_EXECUTION_UNKNOWN" : "FOCUS_COMMAND_NOT_APPLIED", result.ErrorCode);
        Assert.Equal(changesBudget ? ToolExecutionState.Unknown : ToolExecutionState.NotExecuted, result.ExecutionState);
        Assert.Equal(1, owner.ResumeCalls);
    }

    private static FakeFocusSessionService PausedOwner(bool breakPhase)
    {
        var owner = new FakeFocusSessionService();
        owner.Start(FocusPreset.Create(25, 5, 4), "role-a");
        owner.Advance(1500);
        owner.Advance(300);
        if (breakPhase) owner.Advance(1500);
        owner.Advance(17);
        owner.Pause();
        return owner;
    }

    private static IReadOnlyList<IToolProvider> Create(FakeFocusSessionService owner,
        InlineFocusDispatcher dispatcher, string? activeRole) => NativeFocusTools.Create(owner, dispatcher, () => activeRole);

    private static IToolProvider Tool(IReadOnlyList<IToolProvider> tools, string name) =>
        Assert.Single(tools, tool => tool.Descriptor.Name == name);

    private static ToolScope Scope(string roleId = "role-a") => new("conversation-1", roleId, "project-1");

    private static async Task<ToolResult> Invoke(IToolProvider tool, ToolScope scope, object arguments,
        string idempotencyKey, ToolResourceAuthorization? authorization = null, CancellationToken cancellationToken = default)
    {
        authorization ??= ((IToolResourceAuthorizationProvider)tool).GetAuthorization(scope);
        var execution = new ToolExecutionContext("run-1", 1, idempotencyKey, idempotencyKey)
        {
            ResourceAuthorization = authorization,
        };
        return await tool.InvokeAsync(new ToolInvocation(scope, JsonSerializer.SerializeToElement(arguments))
        {
            ExecutionContext = execution,
        }, cancellationToken);
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
        public int ResumeCalls { get; private set; }
        public int StopCalls { get; private set; }
        public bool FailNextPersistence { get; set; }
        public bool IgnoreResume { get; set; }
        public bool ChangeBudgetOnResume { get; set; }
        public Exception? ResumeError { get; set; }
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

        public void Resume()
        {
            ResumeCalls++;
            if (IgnoreResume) return;
            Apply(Current, new FocusCommand.Resume());
            if (ChangeBudgetOnResume) Current = Current with { RemainingSeconds = Current.RemainingSeconds + 1 };
            if (ResumeError is { } error) throw error;
        }
        public void Advance(int seconds) => Apply(Current, new FocusCommand.Elapsed(seconds));
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
