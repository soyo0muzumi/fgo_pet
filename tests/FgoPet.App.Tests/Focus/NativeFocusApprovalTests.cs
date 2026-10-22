using System.Text.Json;
using FgoPet.App.Dialogue;
using FgoPet.App.Focus;
using FgoPet.Core.Focus;
using FgoPet.Extensibility;
using FgoPet.Kernel.Agent;
using FgoPet.Plugin.Focus;
using Xunit;

namespace FgoPet.App.Tests.Focus;

public sealed class NativeFocusApprovalTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Production_policy_waits_for_exact_approval_then_resumes_the_existing_phase(bool breakPhase)
    {
        using var host = new Host(breakPhase);
        await host.StartAsync();
        var before = host.Owner.Current;
        var request = host.Request();

        var waiting = await host.Pipeline().AdvanceAsync(request, host.Intent, default);

        Assert.Equal(ToolExecutionOutcomeKind.WaitingApproval, waiting.Kind);
        Assert.Equal(0, host.Intent.Commits);
        Assert.Equal(0, host.Owner.ResumeCalls);
        Assert.Equal(before, host.Owner.Current);
        Assert.Equal("firstparty.focus", waiting.ApprovalRequest!.Tool.PluginId);
        Assert.Equal("focus.resume", waiting.ApprovalRequest.Tool.ToolName);
        Assert.NotNull(waiting.ApprovalRequest.Tool.Resource?.Fingerprint);
        using var parameters = JsonDocument.Parse(waiting.ApprovalRequest.NormalizedArgumentsJson);
        Assert.Equal(before.SessionId, parameters.RootElement.GetProperty("expectedSessionId").GetString());

        var approved = host.Accept(request, waiting.ApprovalRequest);
        var result = await host.Pipeline().AdvanceAsync(approved, host.Intent, default);
        var repeated = await host.Pipeline().AdvanceAsync(approved, host.Intent, default);

        Assert.Equal(ToolExecutionOutcomeKind.Completed, result.Kind);
        Assert.True(result.Result!.Success);
        Assert.Equal(ToolExecutionState.Committed, result.Result.ExecutionState);
        Assert.Equal(breakPhase ? FocusStatus.Breaking : FocusStatus.Focusing, host.Owner.Current.Status);
        Assert.Equal(before.SessionId, host.Owner.Current.SessionId);
        Assert.Equal(before.CurrentCycle, host.Owner.Current.CurrentCycle);
        Assert.Equal(before.RemainingSeconds, host.Owner.Current.RemainingSeconds);
        Assert.Equal(1, host.Owner.ResumeCalls);
        Assert.Equal(1, host.Intent.Commits);
        Denied(repeated, "TOOL_APPROVAL_STALE");
    }

    [Theory]
    [InlineData("focus.start")]
    [InlineData("focus.pause")]
    [InlineData("focus.stop")]
    public async Task Production_policy_does_not_enable_other_focus_commands(string name)
    {
        using var host = new Host();
        await host.StartAsync();
        var request = host.Request(name);

        var result = await host.Pipeline().AdvanceAsync(request, host.Intent, default);

        Denied(result, "TOOL_AUTHORIZATION_DENIED");
        Assert.Null(result.ApprovalRequest);
        Assert.Equal(0, host.Intent.Commits);
        Assert.Equal(0, host.Owner.CommandCalls);
    }

    [Fact]
    public async Task Production_policy_denies_a_resume_name_from_another_plugin()
    {
        using var host = new Host(pluginId: "fixture.imposter");
        await host.StartAsync();

        var result = await host.Pipeline().AdvanceAsync(host.Request(), host.Intent, default);

        Denied(result, "TOOL_AUTHORIZATION_DENIED");
        Assert.Equal(0, host.Owner.ResumeCalls);
        Assert.Equal(0, host.Intent.Commits);
    }

    [Fact]
    public async Task Production_policy_keeps_focus_get_read_only_and_does_not_prompt()
    {
        using var host = new Host();
        await host.StartAsync();

        var result = await host.Pipeline().AdvanceAsync(host.Request("focus.get"), host.Intent, default);

        Assert.Equal(ToolExecutionOutcomeKind.Completed, result.Kind);
        Assert.True(result.Result!.Success);
        Assert.Equal("paused_focus", result.Result.Payload.GetProperty("status").GetString());
        Assert.Null(result.ApprovalRequest);
        Assert.Equal(0, host.Owner.CommandCalls);
    }

    [Fact]
    public async Task Ask_without_an_approval_broker_cannot_resume()
    {
        using var host = new Host();
        await host.StartAsync();

        var result = await host.Pipeline(includeBroker: false).AdvanceAsync(host.Request(), host.Intent, default);

        Denied(result, "TOOL_AUTHORIZATION_DENIED");
        Assert.Equal(0, host.Owner.ResumeCalls);
        Assert.Equal(0, host.Intent.Commits);
    }

    [Theory]
    [InlineData("expired", "TOOL_APPROVAL_EXPIRED")]
    [InlineData("arguments", "TOOL_APPROVAL_STALE")]
    [InlineData("pause-cycle", "TOOL_APPROVAL_STALE")]
    [InlineData("role", "TOOL_SCOPE_DENIED")]
    public async Task Frozen_approval_rejects_expiry_arguments_or_owner_scope_changes(string change, string error)
    {
        using var host = new Host();
        await host.StartAsync();
        var approved = await host.ApprovedAsync();
        if (change == "expired") host.Time.Now = approved.Approval!.Binding.ExpiresAt;
        else if (change == "arguments") approved = approved with
        {
            Call = approved.Call with { ArgumentsJson = "{\"expectedSessionId\":\"other-session\"}" },
        };
        else if (change == "pause-cycle")
        {
            host.Owner.Resume();
            host.Owner.Pause();
        }
        else host.ActiveRole = "other-role";
        var before = host.Owner.CommandCalls;

        var result = await host.Pipeline().AdvanceAsync(approved, host.Intent, default);

        Denied(result, error);
        Assert.Equal(before, host.Owner.CommandCalls);
        Assert.Equal(0, host.Intent.Commits);
    }

    [Theory]
    [InlineData("pause-cycle", "TOOL_SCOPE_DENIED")]
    [InlineData("role", "TOOL_SCOPE_DENIED")]
    [InlineData("expired", "TOOL_APPROVAL_EXPIRED")]
    public async Task Approval_and_owner_are_rechecked_after_the_execution_intent_yields(string change, string error)
    {
        using var host = new Host();
        await host.StartAsync();
        var approved = await host.ApprovedAsync();
        host.Intent.OnCommit = () =>
        {
            if (change == "pause-cycle") { host.Owner.Resume(); host.Owner.Pause(); }
            else if (change == "role") host.ActiveRole = "other-role";
            else host.Time.Now = approved.Approval!.Binding.ExpiresAt;
        };

        var result = await host.Pipeline().AdvanceAsync(approved, host.Intent, default);

        Denied(result, error);
        Assert.Equal(1, host.Intent.Commits);
        Assert.Equal(change == "pause-cycle" ? 1 : 0, host.Owner.ResumeCalls);
        Assert.Equal(FocusStatus.PausedFocus, host.Owner.Current.Status);
    }

    [Fact]
    public async Task Approved_resume_persistence_failure_is_unknown_not_a_successful_execution()
    {
        using var host = new Host();
        await host.StartAsync();
        var approved = await host.ApprovedAsync();
        host.Owner.FailPersistence = true;

        var result = await host.Pipeline().AdvanceAsync(approved, host.Intent, default);

        Assert.Equal(ToolExecutionOutcomeKind.ExecutionUnknown, result.Kind);
        Assert.False(result.Result!.Success);
        Assert.Equal(ToolExecutionState.Unknown, result.Result.ExecutionState);
        Assert.Equal("FOCUS_EXECUTION_UNKNOWN", result.Result.ErrorCode);
        Assert.Equal(FocusStatus.PausedFocus, host.Owner.Current.Status);
        Assert.Equal(1, host.Owner.ResumeCalls);
    }

    [Fact]
    public async Task Cancellation_during_intent_does_not_resume_the_timer()
    {
        using var host = new Host();
        using var cancellation = new CancellationTokenSource();
        await host.StartAsync();
        var approved = await host.ApprovedAsync();
        host.Intent.OnCommit = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            host.Pipeline().AdvanceAsync(approved, host.Intent, cancellation.Token).AsTask());

        Assert.Equal(1, host.Intent.Commits);
        Assert.Equal(0, host.Owner.ResumeCalls);
        Assert.Equal(FocusStatus.PausedFocus, host.Owner.Current.Status);
    }

    private static void Denied(ToolExecutionOutcome result, string error)
    {
        Assert.Equal(ToolExecutionOutcomeKind.Completed, result.Kind);
        Assert.False(result.Result!.Success);
        Assert.Equal(error, result.Result.ErrorCode);
        Assert.Equal(ToolExecutionState.NotExecuted, result.Result.ExecutionState);
    }

    private sealed class Host : IDisposable
    {
        public ToolScope Scope { get; } = new("conversation", "role", "project");
        public string ActiveRole { get; set; } = "role";
        public Owner Owner { get; }
        public ManualTime Time { get; } = new();
        public Intent Intent { get; } = new();
        private ApprovalBroker Broker { get; }
        private PluginRuntime Runtime { get; }
        private ToolRegistry Registry { get; }
        public Host(bool breakPhase = false, string pluginId = "firstparty.focus")
        {
            Owner = new(breakPhase);
            Broker = new(Time);
            var providers = NativeFocusTools.Create(Owner, new InlineDispatcher(), () => ActiveRole);
            var catalog = PluginCatalog.Create([new Plugin(pluginId, providers)]);
            Runtime = new(catalog);
            Registry = new(catalog, Runtime);
        }
        public async Task StartAsync() => Assert.True((await Runtime.StartAsync(default)).Succeeded);
        public ToolExecutionPipeline Pipeline(bool includeBroker = true) =>
            new(Registry, new ToolExecutor(Registry, Scope, new Fence()), new NativeSceneToolPolicy(), includeBroker ? Broker : null);
        public ToolExecutionRequest Request(string name = "focus.resume") =>
            new(new("run", "root-user", Scope, "model", 1), 1, new("call", name, name switch
            {
                "focus.get" => "{}",
                "focus.start" => "{\"focusMinutes\":25,\"breakMinutes\":5,\"cycles\":4}",
                _ => JsonSerializer.Serialize(new { expectedSessionId = Owner.Current.SessionId }),
            })) { CheckpointRevision = 1 };
        public ToolExecutionRequest Accept(ToolExecutionRequest request, ApprovalRequest approval)
        {
            Assert.Equal(ApprovalDecision.Allow, Broker.Validate(approval,
                new(request.Identity.RunId, approval.Binding.RequestId, approval.Binding.WaitingRevision, ApprovalDecision.Allow), Scope));
            // Represents the coordinator's persisted accepted user reply, not an authority field from the model.
            return request with { Approval = approval };
        }
        public async Task<ToolExecutionRequest> ApprovedAsync()
        {
            var request = Request();
            var waiting = await Pipeline().AdvanceAsync(request, Intent, default);
            Assert.Equal(ToolExecutionOutcomeKind.WaitingApproval, waiting.Kind);
            return Accept(request, waiting.ApprovalRequest!);
        }
        public void Dispose() => Runtime.Dispose();
    }

    private sealed class Plugin(string id, IReadOnlyList<IToolProvider> providers) : IFgoPetPlugin
    {
        public PluginManifest Manifest { get; } = new(id, "1.0.0", 1, []);
        public PluginContributions Contributions { get; } = new([.. providers], [], []);
        public ValueTask StartAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask StopAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Owner : IFocusSessionService
    {
        private DateTimeOffset at = DateTimeOffset.UnixEpoch;
        public Owner(bool breakPhase) => Current = FocusSession.Start("session", "role", FocusPreset.Create(25, 5, 4), at) with
        {
            Status = breakPhase ? FocusStatus.PausedBreak : FocusStatus.PausedFocus,
            Phase = breakPhase ? FocusPhase.Break : FocusPhase.Focus,
            RemainingSeconds = breakPhase ? 240 : 1440,
            PhaseElapsedSeconds = 60,
            CurrentCycle = 2,
        };
        public FocusSession Current { get; private set; }
        public int ResumeCalls { get; private set; }
        public int CommandCalls { get; private set; }
        public bool FailPersistence { get; set; }
        public event EventHandler? SnapshotChanged;
        public event EventHandler? PersistenceFailed;
        public void Resume()
        {
            ResumeCalls++;
            Apply(new FocusCommand.Resume());
            if (!FailPersistence) return;
            Current = Current.RestorePaused();
            PersistenceFailed?.Invoke(this, EventArgs.Empty);
        }
        public void Pause() => Apply(new FocusCommand.Pause());
        public void Stop() => Apply(new FocusCommand.Stop());
        public void Start(FocusPreset preset, string servantId) => Apply(new FocusCommand.Start(preset, servantId));
        public void Tick() { }
        public void Restore() { }
        private void Apply(FocusCommand command)
        {
            CommandCalls++;
            at = at.AddSeconds(1);
            Current = FocusStateMachine.Apply(Current, command, at).Session;
            SnapshotChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class Intent : IToolExecutionIntent
    {
        public int Commits { get; private set; }
        public Action? OnCommit { get; set; }
        public async ValueTask CommitStartedAsync(ToolDescriptor descriptor, CancellationToken token)
        {
            await Task.Yield();
            token.ThrowIfCancellationRequested();
            Commits++;
            OnCommit?.Invoke();
        }
    }
    private sealed class InlineDispatcher : IFocusNativeDispatcher
    {
        public T Invoke<T>(Func<T> operation) => operation();
        public ValueTask<T> InvokeAsync<T>(Func<T> operation, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return ValueTask.FromResult(operation());
        }
    }
    private sealed class Fence : IAgentRunFence
    {
        public void EnsureCurrent(AgentRunIdentity identity, CancellationToken token) => token.ThrowIfCancellationRequested();
    }
    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
