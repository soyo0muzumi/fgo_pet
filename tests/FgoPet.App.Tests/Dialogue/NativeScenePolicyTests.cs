using System.IO;
using System.Text.Json;
using FgoPet.App.Dialogue;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Kernel.Agent;
using FgoPet.Plugin.Reminders;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

public sealed class NativeScenePolicyTests
{
    [Theory]
    [InlineData("reminders", "reminder.create", ToolEffect.Command, ToolPolicyDecision.Ask)]
    [InlineData("reminders", "reminder.cancel", ToolEffect.Command, ToolPolicyDecision.Ask)]
    [InlineData("firstparty.focus", "focus.resume", ToolEffect.Command, ToolPolicyDecision.Ask)]
    [InlineData("other", "focus.resume", ToolEffect.Command, ToolPolicyDecision.Deny)]
    [InlineData("other", "reminder.create", ToolEffect.Command, ToolPolicyDecision.Deny)]
    [InlineData("firstparty.focus", "focus.start", ToolEffect.Command, ToolPolicyDecision.Deny)]
    [InlineData("firstparty.focus", "focus.pause", ToolEffect.Command, ToolPolicyDecision.Deny)]
    [InlineData("firstparty.focus", "focus.stop", ToolEffect.Command, ToolPolicyDecision.Deny)]
    [InlineData("todo", "todo.create", ToolEffect.Command, ToolPolicyDecision.Deny)]
    [InlineData("shell", "shell.run", ToolEffect.Command, ToolPolicyDecision.Deny)]
    [InlineData("image", "image.read", ToolEffect.ReadOnly, ToolPolicyDecision.Allow)]
    [InlineData("other", "existing.proposal", ToolEffect.Proposal, ToolPolicyDecision.Allow)]
    public void Production_policy_asks_only_for_exact_scene_commands_without_expanding_other_mutations(
        string plugin, string tool, ToolEffect effect, ToolPolicyDecision expected)
    {
        var provider = new NeverInvoked(tool, effect);
        Assert.Equal(expected, new NativeSceneToolPolicy().Decide(null!, new(plugin, provider.Descriptor, provider)));
    }

    [Fact]
    public async Task Real_reminder_owner_is_reachable_only_after_frozen_approval_and_replays_once()
    {
        await using var host = await Host.Create();
        var request = host.CreateRequest();
        var waiting = await host.Pipeline.AdvanceAsync(request, host.Intent, default);
        Assert.Equal(ToolExecutionOutcomeKind.WaitingApproval, waiting.Kind);
        Assert.Empty(host.Owner.List(host.Scope)); Assert.Equal(0, host.Intent.Commits);
        var approval = waiting.ApprovalRequest!;
        Assert.Contains("synthetic reminder", approval.Tool.BusinessConfirmation!.PlainTextSummary);
        Assert.Contains("时区：UTC", approval.Tool.BusinessConfirmation.PlainTextSummary);
        Assert.Contains("24 小时", approval.Tool.BusinessConfirmation.PlainTextSummary);
        Assert.Equal(ApprovalDecision.Allow, host.Broker.Validate(approval,
            new(request.Identity.RunId, approval.Binding.RequestId, approval.Binding.WaitingRevision, ApprovalDecision.Allow), host.Scope));
        // Stands in for the coordinator's persisted accepted reply, never a web/model supplied permit.
        var approved = request with { Approval = approval };
        Assert.True((await host.Pipeline.AdvanceAsync(approved, host.Intent, default)).Result!.Success);
        Assert.True((await host.Pipeline.AdvanceAsync(approved, host.Intent, default)).Result!.Success);
        var item = Assert.Single(host.Owner.List(host.Scope));
        var cancel = host.Request("reminder.cancel", new { id = item.Id, expectedRevision = item.Revision }, "cancel");
        var cancelWait = await host.Pipeline.AdvanceAsync(cancel, host.Intent, default);
        var summary = cancelWait.ApprovalRequest!.Tool.BusinessConfirmation!.PlainTextSummary;
        Assert.Contains(item.Message, summary); Assert.Contains(item.Id, summary); Assert.Contains("版本：1", summary);
        Assert.True((await host.Pipeline.AdvanceAsync(cancel with { Approval = cancelWait.ApprovalRequest }, host.Intent, default)).Result!.Success);
        Assert.Equal(ReminderStatus.Cancelled, Assert.Single(host.Owner.List(host.Scope)).Status);
    }

    [Theory]
    [InlineData("arguments")]
    [InlineData("summary")]
    [InlineData("scope")]
    [InlineData("expired")]
    [InlineData("after-intent")]
    public async Task Approval_changes_and_revocation_never_create_a_reminder(string change)
    {
        await using var host = await Host.Create();
        var request = host.CreateRequest();
        var wait = await host.Pipeline.AdvanceAsync(request, host.Intent, default);
        request = request with { Approval = wait.ApprovalRequest };
        if (change == "arguments") request = request with { Call = request.Call with { ArgumentsJson = "{\"message\":\"changed\",\"dueAt\":\"2026-10-21T09:00:00Z\",\"timeZoneId\":\"UTC\"}" } };
        if (change == "summary") request = request with { Approval = request.Approval! with { Tool = request.Approval.Tool with
            { BusinessConfirmation = request.Approval.Tool.BusinessConfirmation! with { PlainTextSummary = "changed" } } } };
        if (change == "scope") host.Current = false;
        if (change == "expired") host.Clock.Advance(TimeSpan.FromMinutes(16));
        if (change == "after-intent") host.Intent.OnCommit = () => host.Current = false;
        Assert.False((await host.Pipeline.AdvanceAsync(request, host.Intent, default)).Result!.Success);
        Assert.Empty(host.Owner.List(host.Scope));
    }

    [Fact]
    public async Task A_cancel_claimed_after_confirmation_is_not_reported_as_cancelled()
    {
        await using var host = await Host.Create();
        var item = host.Owner.Create(host.Scope, "fixture", new('A', 64), new("synthetic reminder", host.Clock.GetUtcNow().AddMinutes(1), "UTC"));
        var cancel = host.Request("reminder.cancel", new { id = item.Id, expectedRevision = 1 }, "cancel");
        var wait = await host.Pipeline.AdvanceAsync(cancel, host.Intent, default);
        host.Clock.Advance(TimeSpan.FromMinutes(2)); Assert.NotNull(host.Owner.ClaimNext());
        var outcome = await host.Pipeline.AdvanceAsync(cancel with { Approval = wait.ApprovalRequest }, host.Intent, default);
        Assert.False(outcome.Result!.Success);
        Assert.Equal(ReminderStatus.Dispatching, Assert.Single(host.Owner.List(host.Scope)).Status);
        Assert.Equal(0, host.Intent.Commits);
    }

    private sealed class Host : IAsyncDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "fgo-scene-policy-" + Guid.NewGuid().ToString("N"));
        public ToolScope Scope { get; } = new("conversation", "role", "project");
        public FakeTimeProvider Clock { get; } = new(DateTimeOffset.Parse("2026-10-21T08:00:00Z"));
        public bool Current = true;
        public ReminderService Owner { get; }
        public ApprovalBroker Broker { get; }
        public Intent Intent { get; } = new();
        public ToolExecutionPipeline Pipeline { get; }
        private readonly PluginRuntime runtime;
        private readonly RemindersPlugin plugin;
        private readonly IDisposable foreground;
        private Host()
        {
            Owner = new(new RuntimeDatabase(Path.Combine(directory, "runtime.db"), pooling: false), Clock);
            Broker = new(Clock);
            var lifetime = new DialogueContextLifetime();
            foreground = lifetime.Acquire(default); // Isolate explicit owner claims from the live periodic worker.
            var scheduler = new ReminderScheduler(Owner, new NeverNotify(), lifetime, Clock);
            plugin = new(Owner, scheduler, scope => Current && scope == Scope, Clock);
            var catalog = PluginCatalog.Create([plugin]); runtime = new(catalog);
            var registry = new ToolRegistry(catalog, runtime);
            Pipeline = new(registry, new(registry, Scope, new Fence()), new NativeSceneToolPolicy(), Broker);
        }
        public static async Task<Host> Create()
        {
            var host = new Host(); Assert.True((await host.runtime.StartAsync(default)).Succeeded);
            await host.plugin.OnApplicationReadyAsync(new(true), default); return host;
        }
        public ToolExecutionRequest CreateRequest() => Request("reminder.create", new { message = "synthetic reminder", dueAt = "2026-10-21T09:00:00Z", timeZoneId = "UTC" });
        public ToolExecutionRequest Request(string name, object args, string call = "create") =>
            new(new("run", "user", Scope, "model", 1), 1, new(call, name, JsonSerializer.Serialize(args)));
        public async ValueTask DisposeAsync() { await runtime.DisposeAsync(); foreground.Dispose(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    private sealed class Intent : IToolExecutionIntent
    {
        public int Commits; public Action? OnCommit;
        public ValueTask CommitStartedAsync(ToolDescriptor descriptor, CancellationToken token)
        { Commits++; OnCommit?.Invoke(); return ValueTask.CompletedTask; }
    }
    private sealed class Fence : IAgentRunFence { public void EnsureCurrent(AgentRunIdentity identity, CancellationToken token) => token.ThrowIfCancellationRequested(); }
    private sealed class NeverInvoked(string name, ToolEffect effect) : IToolProvider
    {
        public ToolDescriptor Descriptor { get; } = new(name, "synthetic", "{\"type\":\"object\"}", effect);
        public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken token) => throw new NotImplementedException();
    }
    private sealed class NeverNotify : IReminderNotifier
    {
        public ValueTask<ReminderNotificationOutcome> NotifyAsync(Reminder reminder, CancellationToken token) => throw new InvalidOperationException("No real notification in policy tests");
    }
}
