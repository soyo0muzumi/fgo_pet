using System.Text.Json;
using FgoPet.App.Dialogue;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Plugin.Reminders;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace FgoPet.Plugin.Reminders.Tests;

public sealed class ReminderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "fgo-reminder-test-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.Parse("2026-10-21T08:00:00Z"));
    private static readonly ToolScope Scope = new("conversation", "mash", null);
    private string DatabasePath => Path.Combine(_directory, "state.db");
    private ReminderService Open() => new(new RuntimeDatabase(DatabasePath, pooling: false), _clock);
    private Reminder Add(ReminderService owner, string key = "fixture") => owner.Create(Scope, key, new('A', 64), new("synthetic reminder", _clock.GetUtcNow().AddMinutes(1), "UTC"));
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    [Fact]
    public void Notification_text_fits_the_real_Windows_balloon_without_silent_truncation()
    {
        var owner = Open(); var at = _clock.GetUtcNow().AddMinutes(1);
        Assert.Equal(255, owner.Validate(new(new('x', 255), at, "UTC")).Message.Length);
        Assert.Equal("REMINDER_INVALID_ARGUMENTS", Assert.Throws<ReminderException>(() => owner.Validate(new(new('x', 256), at, "UTC"))).Code);
        Assert.Equal("REMINDER_INVALID_ARGUMENTS", Assert.Throws<ReminderException>(() => owner.Validate(new("unpaired\ud800", at, "UTC"))).Code);
        Assert.Equal("😀", owner.Validate(new("😀", at, "UTC")).Message);
    }

    [Fact]
    public async Task Pending_items_remain_discoverable_after_large_history_and_history_is_pageable()
    {
        var owner = Open();
        for (var i = 0; i < 105; i++)
        {
            var item = Add(owner, "historical-" + i);
            owner.Cancel(Scope, "cancel-" + i, new('A', 64), item.Id, item.Revision);
        }
        var pending = Add(owner, "future");
        await using var scheduler = new ReminderScheduler(owner, new Sink(), new DialogueContextLifetime(), _clock);
        await using var plugin = new RemindersPlugin(owner, scheduler, scope => scope == Scope, _clock);
        await plugin.StartAsync(default); await plugin.OnApplicationReadyAsync(new(true), default);
        var tool = plugin.Contributions.Tools.Single(t => t.Descriptor.Name == "reminder.list");
        var first = await tool.InvokeAsync(new(Scope, JsonSerializer.SerializeToElement(new { })), default);
        Assert.True(first.Success); Assert.Equal(pending.Id, first.Payload.GetProperty("items")[0].GetProperty("id").GetString());
        Assert.Equal(20, first.Payload.GetProperty("nextOffset").GetInt32());
        var last = await tool.InvokeAsync(new(Scope, JsonSerializer.SerializeToElement(new { offset = 100, limit = 20 })), default);
        Assert.Equal(6, last.Payload.GetProperty("items").GetArrayLength());
        Assert.False(last.Payload.GetProperty("truncated").GetBoolean());
        Assert.Empty(owner.List(Scope with { ConversationId = "other" }));
    }

    [Fact]
    public void New_and_migrated_databases_reopen_with_durable_exact_receipts()
    {
        var first = Add(Open());
        var reopened = Open();
        Assert.Equal(first, Assert.Single(reopened.List(Scope)));
        _clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(first, reopened.Create(Scope, "fixture", new('A', 64), new("ignored replay", first.RequestedTime, "UTC")));
        Assert.Equal("REMINDER_IDEMPOTENCY_CONFLICT", Assert.Throws<ReminderException>(() =>
            reopened.Create(Scope, "fixture", new('B', 64), new("different", first.RequestedTime, "UTC"))).Code);
    }

    [Fact]
    public void Timezone_preserves_absolute_instant_and_rejects_nonexistent_or_mismatched_time()
    {
        var owner = Open();
        var date = DateTimeOffset.Parse("2026-11-01T01:30:00-04:00");
        var item = owner.Create(Scope, "dst", new('A', 64), new("fall back early occurrence", date, "America/New_York"));
        Assert.Equal(DateTimeOffset.Parse("2026-11-01T05:30:00Z"), item.DueAtUtc);
        Assert.Equal(date, item.RequestedTime);
        Assert.Equal("REMINDER_TIMEZONE_MISMATCH", Assert.Throws<ReminderException>(() => owner.Validate(new("bad", date.ToOffset(TimeSpan.Zero), "America/New_York"))).Code);
        Assert.Equal("REMINDER_TIMEZONE_MISMATCH", Assert.Throws<ReminderException>(() => owner.Validate(new("gap", DateTimeOffset.Parse("2027-03-14T02:30:00-05:00"), "America/New_York"))).Code);
        Assert.Equal("REMINDER_TIMEZONE_INVALID", Assert.Throws<ReminderException>(() => owner.Validate(new("bad zone", date, "not-a-zone"))).Code);
    }

    [Fact]
    public void Cancel_is_revision_bound_and_wins_before_claim_only()
    {
        var owner = Open(); var first = Add(owner);
        Assert.Equal("REMINDER_VERSION_CONFLICT", Assert.Throws<ReminderException>(() => owner.Cancel(Scope, "cancel", new('A', 64), first.Id, 2)).Code);
        var cancelled = owner.Cancel(Scope, "cancel", new('A', 64), first.Id, 1);
        Assert.Equal(ReminderStatus.Cancelled, cancelled.Status);
        _clock.Advance(TimeSpan.FromMinutes(2)); Assert.Null(owner.ClaimNext());
        var second = Add(owner, "second"); _clock.Advance(TimeSpan.FromMinutes(2));
        var claimed = owner.ClaimNext()!;
        Assert.Equal(second.Id, claimed.Id);
        Assert.Equal("REMINDER_NOT_PENDING", Assert.Throws<ReminderException>(() => owner.Cancel(Scope, "late-cancel", new('A', 64), claimed.Id, claimed.Revision)).Code);
    }

    [Fact]
    public async Task Restart_catches_up_once_and_concurrent_pulses_do_not_repeat()
    {
        Add(Open()); _clock.Advance(TimeSpan.FromHours(1));
        var owner = Open(); var sink = new Sink();
        await using var scheduler = new ReminderScheduler(owner, sink, new DialogueContextLifetime(), _clock);
        await Task.WhenAll(scheduler.PulseAsync(), scheduler.PulseAsync(), scheduler.PulseAsync());
        Assert.Equal(1, sink.Calls); Assert.Equal(ReminderStatus.Submitted, Assert.Single(owner.List(Scope)).Status);
        await using var reopened = new ReminderScheduler(Open(), sink, new DialogueContextLifetime(), _clock);
        await reopened.PulseAsync(); Assert.Equal(1, sink.Calls);
    }

    [Fact]
    public async Task Older_than_one_day_expires_instead_of_flooding_notifications()
    {
        var owner = Open(); Add(owner); _clock.Advance(TimeSpan.FromDays(2)); var sink = new Sink();
        await using var scheduler = new ReminderScheduler(owner, sink, new DialogueContextLifetime(), _clock);
        await scheduler.PulseAsync();
        Assert.Equal(0, sink.Calls); Assert.Equal(ReminderStatus.Expired, Assert.Single(owner.List(Scope)).Status);
    }

    [Fact]
    public async Task Crash_after_claim_is_unknown_and_never_blindly_replayed()
    {
        var owner = Open(); Add(owner); _clock.Advance(TimeSpan.FromMinutes(2)); Assert.NotNull(owner.ClaimNext());
        var reopened = Open(); reopened.RecoverInterrupted(); var sink = new Sink();
        await using var scheduler = new ReminderScheduler(reopened, sink, new DialogueContextLifetime(), _clock);
        await scheduler.PulseAsync();
        Assert.Equal(0, sink.Calls); Assert.Equal(ReminderStatus.Unknown, Assert.Single(reopened.List(Scope)).Status);
    }

    [Fact]
    public async Task Sink_failures_are_observable_and_not_retried()
    {
        var owner = Open(); Add(owner); _clock.Advance(TimeSpan.FromMinutes(2));
        var sink = new Sink { Outcome = ReminderNotificationOutcome.Unavailable };
        await using var scheduler = new ReminderScheduler(owner, sink, new DialogueContextLifetime(), _clock);
        await scheduler.PulseAsync(); await scheduler.PulseAsync();
        Assert.Equal(1, sink.Calls); Assert.Equal(ReminderStatus.Failed, Assert.Single(owner.List(Scope)).Status);
    }

    [Fact]
    public async Task Cancellation_and_unknown_notification_outcomes_do_not_claim_success()
    {
        var owner = Open(); Add(owner); _clock.Advance(TimeSpan.FromMinutes(2)); var sink = new Sink { Throws = true };
        await using var scheduler = new ReminderScheduler(owner, sink, new DialogueContextLifetime(), _clock);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scheduler.PulseAsync(cts.Token));
        Assert.Equal(0, sink.Calls); Assert.Equal(ReminderStatus.Pending, Assert.Single(owner.List(Scope)).Status);
        await scheduler.PulseAsync(); await scheduler.PulseAsync();
        Assert.Equal(1, sink.Calls); Assert.Equal(ReminderStatus.Unknown, Assert.Single(owner.List(Scope)).Status);
    }

    [Fact]
    public async Task Privacy_suspension_drains_notification_before_forgetting()
    {
        var owner = Open(); Add(owner); _clock.Advance(TimeSpan.FromMinutes(2));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new BlockingSink(entered); var lifetime = new DialogueContextLifetime();
        await using var scheduler = new ReminderScheduler(owner, sink, lifetime, _clock);
        var pulse = scheduler.PulseAsync(); await entered.Task;
        using (await lifetime.SuspendAsync(default))
        {
            owner.ForgetConversation(Scope.ConversationId, Scope.RoleId);
            Assert.Empty(owner.List(Scope));
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pulse);
        await scheduler.PulseAsync(); Assert.Empty(owner.List(Scope));
    }

    [Fact]
    public void Restore_does_not_rearm_historical_notifications_or_touch_live_state()
    {
        var owner = Open(); Add(owner);
        var staging = Path.Combine(_directory, "staged.db");
        var stagedOwner = new ReminderService(new RuntimeDatabase(staging, pooling: false), _clock); Add(stagedOwner);
        owner.NormalizeRestoredCopy(staging);
        Assert.Empty(stagedOwner.List(Scope)); Assert.Single(owner.List(Scope));
        Assert.Throws<InvalidOperationException>(() => owner.NormalizeRestoredCopy(DatabasePath));
        owner.ForgetAll(); Assert.Empty(owner.List(Scope));
    }

    [Fact]
    public async Task Commands_require_exact_confirmation_and_active_scope_and_are_idempotent()
    {
        var owner = Open(); var current = true;
        await using var scheduler = new ReminderScheduler(owner, new Sink(), new DialogueContextLifetime(), _clock);
        await using var plugin = new RemindersPlugin(owner, scheduler, scope => current && scope == Scope, _clock);
        await plugin.StartAsync(default); await plugin.OnApplicationReadyAsync(new(true), default);
        var tool = plugin.Contributions.Tools.Single(t => t.Descriptor.Name == "reminder.create");
        var args = JsonSerializer.SerializeToElement(new { message = "synthetic", dueAt = "2026-10-21T08:05:00Z", timeZoneId = "UTC" });
        var invocation = new ToolInvocation(Scope, args) { ExecutionContext = new("run", 1, "call", "key") };
        Assert.Equal("REMINDER_CONFIRMATION_REQUIRED", (await tool.InvokeAsync(invocation, default)).ErrorCode);
        var confirmationOwner = (IToolBusinessConfirmationProvider)tool;
        var binding = confirmationOwner.PrepareConfirmation(invocation, default);
        var confirmed = invocation with { ExecutionContext = invocation.ExecutionContext with { BusinessConfirmation = binding } };
        var edited = confirmed with { Arguments = JsonSerializer.SerializeToElement(new { message = "changed", dueAt = "2026-10-21T08:05:00Z", timeZoneId = "UTC" }) };
        Assert.Equal("REMINDER_CONFIRMATION_STALE", (await tool.InvokeAsync(edited, default)).ErrorCode);
        current = false;
        Assert.Equal("REMINDER_SCOPE_DENIED", (await tool.InvokeAsync(confirmed, default)).ErrorCode);
        current = true;
        Assert.True((await tool.InvokeAsync(confirmed, default)).Success);
        Assert.True((await tool.InvokeAsync(confirmed, default)).Success);
        Assert.Single(owner.List(Scope));
    }

    [Fact]
    public async Task Reminder_confirmation_quotes_multiline_and_directional_text_as_data()
    {
        var owner = Open(); await using var scheduler = new ReminderScheduler(owner, new Sink(), new DialogueContextLifetime(), _clock);
        await using var plugin = new RemindersPlugin(owner, scheduler, _ => true, _clock);
        await plugin.StartAsync(default); await plugin.OnApplicationReadyAsync(new(true), default);
        var tool = (IToolBusinessConfirmationProvider)plugin.Contributions.Tools.Single(t => t.Descriptor.Name == "reminder.create");
        var message = "第一行\n时区：伪造\t\u202e第二行";
        var invocation = new ToolInvocation(Scope, JsonSerializer.SerializeToElement(new { message, dueAt = "2026-10-21T08:05:00Z", timeZoneId = "UTC" }))
            { ExecutionContext = new("run", 1, "call", "key") };
        var summary = tool.PrepareConfirmation(invocation, default).PlainTextSummary!;
        Assert.Contains("第一行", summary); Assert.Contains("\\n", summary); Assert.Contains("\\t", summary);
        Assert.DoesNotContain("\u202e", summary, StringComparison.Ordinal); Assert.Contains("\\u202E", summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\n时区：UTC\n", summary); Assert.Empty(owner.List(Scope));
    }

    [Theory]
    [InlineData("2026-10-21T08:05:00")]
    [InlineData("2026-10-21 08:05:00")]
    [InlineData("2026-10-21T08:05:00+08:00")]
    public async Task Tool_rejects_implicit_or_mismatched_offsets(string dueAt)
    {
        var owner = Open(); await using var scheduler = new ReminderScheduler(owner, new Sink(), new DialogueContextLifetime(), _clock);
        await using var plugin = new RemindersPlugin(owner, scheduler, _ => true, _clock);
        await plugin.StartAsync(default); await plugin.OnApplicationReadyAsync(new(true), default);
        var tool = (IToolBusinessConfirmationProvider)plugin.Contributions.Tools.Single(t => t.Descriptor.Name == "reminder.create");
        var invocation = new ToolInvocation(Scope, JsonSerializer.SerializeToElement(new { message = "synthetic", dueAt, timeZoneId = "UTC" })) { ExecutionContext = new("run", 1, "call", "key") };
        Assert.Throws<ToolBusinessConfirmationException>(() => tool.PrepareConfirmation(invocation, default)); Assert.Empty(owner.List(Scope));
    }

    [Fact]
    public async Task Plugin_waits_for_global_storage_readiness_without_early_database_mutation()
    {
        var owner = Open(); var sink = new Sink();
        await using var scheduler = new ReminderScheduler(owner, sink, new DialogueContextLifetime(), _clock);
        await using var plugin = new RemindersPlugin(owner, scheduler, _ => true, _clock);
        var list = plugin.Contributions.Tools.Single(t => t.Descriptor.Name == "reminder.list");
        var invocation = new ToolInvocation(Scope, JsonSerializer.SerializeToElement(new { }));
        await plugin.OnApplicationReadyAsync(new(true), default);
        await plugin.StartAsync(default);
        Assert.False(File.Exists(DatabasePath));
        Assert.Equal("REMINDER_UNAVAILABLE", (await list.InvokeAsync(invocation, default)).ErrorCode);
        await plugin.OnApplicationReadyAsync(new(false), default);
        Assert.False(File.Exists(DatabasePath));
        Assert.Equal("REMINDER_UNAVAILABLE", (await list.InvokeAsync(invocation, default)).ErrorCode);
        new RuntimeDatabaseMigrator(new RuntimeDatabase(DatabasePath, pooling: false)).Migrate();
        await plugin.OnApplicationReadyAsync(new(true), default);
        await plugin.OnApplicationReadyAsync(new(true), default);
        Assert.True((await list.InvokeAsync(invocation, default)).Success);
        await plugin.StopAsync(default);
        Assert.Equal("REMINDER_UNAVAILABLE", (await list.InvokeAsync(invocation, default)).ErrorCode);
        Assert.Equal(0, sink.Calls);
    }

    [Fact]
    public async Task Shutdown_drains_explicit_pulses_and_stopped_worker_cannot_restart()
    {
        var owner = Open(); Add(owner); _clock.Advance(TimeSpan.FromMinutes(2));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scheduler = new ReminderScheduler(owner, new BlockingSink(entered), new DialogueContextLifetime(), _clock);
        var pulse = scheduler.PulseAsync(); await entered.Task;
        await scheduler.DisposeAsync(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pulse);
        Assert.Throws<InvalidOperationException>(() => scheduler.Start(default));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scheduler.PulseAsync());
    }

    [Fact]
    public async Task Synchronous_container_teardown_cancels_inflight_dispatch_and_preserves_async_drain()
    {
        var owner = Open(); Add(owner); _clock.Advance(TimeSpan.FromMinutes(2));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scheduler = new ReminderScheduler(owner, new BlockingSink(entered), new DialogueContextLifetime(), _clock);
        var plugin = new RemindersPlugin(owner, scheduler, _ => true, _clock);
        var pulse = scheduler.PulseAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.IsAssignableFrom<IDisposable>(plugin).Dispose();
            Assert.IsAssignableFrom<IDisposable>(scheduler).Dispose();
        }
        finally
        {
            await plugin.DisposeAsync();
            await scheduler.DisposeAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pulse);
        }
        Assert.Equal(ReminderStatus.Unknown, Assert.Single(owner.List(Scope)).Status);
        Assert.Throws<InvalidOperationException>(() => scheduler.Start(default));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scheduler.PulseAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Synchronous_exit_returns_before_slow_cancellation_callbacks_and_async_exit_drains_them(
        bool processExit, bool callbackThrows)
    {
        var owner = Open(); Add(owner); _clock.Advance(TimeSpan.FromMinutes(2));
        using var process = new CancellationTokenSource();
        using var release = new ManualResetEventSlim();
        var sink = new SlowCancellationSink(release, callbackThrows);
        var scheduler = new ReminderScheduler(owner, sink, new DialogueContextLifetime(), _clock);
        scheduler.Start(process.Token);
        Task? exit = null;
        try
        {
            await sink.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            exit = Task.Run(() => { if (processExit) process.Cancel(); else scheduler.Dispose(); });
            await sink.CancellationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await exit.WaitAsync(TimeSpan.FromSeconds(2));
            var drain = scheduler.DisposeAsync().AsTask();
            Assert.False(drain.IsCompleted);
            release.Set();
            await drain.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(ReminderStatus.Unknown, Assert.Single(owner.List(Scope)).Status);
            Assert.Throws<InvalidOperationException>(() => scheduler.Start(default));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scheduler.PulseAsync());
        }
        finally
        {
            release.Set();
            if (exit is not null) try { await exit; } catch (AggregateException) { }
            try { await scheduler.DisposeAsync(); } catch (AggregateException) { }
        }
    }

    private sealed class SlowCancellationSink(ManualResetEventSlim release, bool callbackThrows) : IReminderNotifier
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<ReminderNotificationOutcome> NotifyAsync(Reminder reminder, CancellationToken token)
        {
            using var registration = token.Register(() =>
            {
                CancellationEntered.TrySetResult();
                release.Wait();
                if (callbackThrows) throw new InvalidOperationException("synthetic callback failure");
            });
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return ReminderNotificationOutcome.NotAttempted;
        }
    }

    private sealed class Sink : IReminderNotifier
    {
        public int Calls; public bool Throws; public ReminderNotificationOutcome Outcome = ReminderNotificationOutcome.Submitted;
        public ValueTask<ReminderNotificationOutcome> NotifyAsync(Reminder reminder, CancellationToken token)
        { Calls++; if (Throws) throw new IOException("synthetic failure"); return ValueTask.FromResult(Outcome); }
    }
    private sealed class BlockingSink(TaskCompletionSource entered) : IReminderNotifier
    {
        public async ValueTask<ReminderNotificationOutcome> NotifyAsync(Reminder reminder, CancellationToken token)
        { entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return ReminderNotificationOutcome.NotAttempted; }
    }
}
