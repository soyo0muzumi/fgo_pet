using System.IO;
using System.Text.Json;
using FgoPet.App.Companion;
using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Settings;
using FgoPet.Extensibility;
using FgoPet.Kernel.Agent;
using FgoPet.Kernel.Companion;
using FgoPet.Platform.WindowText;
using Xunit;

namespace FgoPet.App.Tests.Companion;

public sealed class CompanionAwarenessHostTests
{
    [Fact]
    public async Task Default_sampling_reads_internal_activity_without_private_sources_or_model_dispatch()
    {
        await using var fixture = new Fixture();
        await fixture.StartAsync();
        var status = fixture.Host.GetStatus();
        Assert.True(status.Available);
        Assert.False(status.ModelApprovalCurrent);
        Assert.Equal("focus.state", Assert.Single(fixture.Snapshot.Observations).SourceId);
        Assert.True(fixture.ActivityReads > 0);
        Assert.Equal(0, fixture.ApplicationReads);
        Assert.Equal(0, fixture.Windows.ListCalls);
        Assert.Empty(fixture.Windows.Reads);
        Assert.False(fixture.Host.ApplicationsAuthorized(fixture.Scope));
        Assert.Null(fixture.Host.Capture());
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Host.ListWindowsAsync(status.ContextToken, default).AsTask());

        fixture.Coordinator.NotifyChanged("focus.completed");
        await fixture.Coordinator.PulseAsync();
        Assert.Empty(fixture.Model.Requests);
        Assert.Equal(0, fixture.Host.GetStatus().RequestsUsed);
    }

    [Fact]
    public async Task Approved_sampling_schedules_a_real_coordinator_and_projects_its_interpretation_only_to_later_context()
    {
        await using var fixture = new Fixture();
        await fixture.StartAsync();
        await fixture.ConfigureAsync(new(true, true, true, 3));
        Assert.True(fixture.Host.ApplicationsAuthorized(fixture.Scope));
        Assert.Contains(fixture.Snapshot.Observations, item => item.SourceId == "desktop.apps");
        Assert.DoesNotContain(fixture.Snapshot.Observations, item => item.IsInterpretation);

        await fixture.Coordinator.PulseAsync();
        var request = Assert.Single(fixture.Model.Requests);
        Assert.Equal(fixture.Scope, request.Configuration.Context.Scope);
        Assert.All(request.Configuration.Context.Observations, item => Assert.False(item.IsInterpretation));
        Assert.Equal(1, fixture.Model.Dispatches);
        var note = Assert.Single(fixture.Snapshot.Observations.Where(item => item.IsInterpretation));
        Assert.Contains("tentative_not_user_confirmed", note.Content);
        Assert.StartsWith("activity.", note.SourceId);
        Assert.All(fixture.Host.Capture()!.Context.Observations, item => Assert.False(item.IsInterpretation));
        var status = fixture.Host.GetStatus();
        Assert.Equal(1, status.RequestsUsed);
        Assert.Equal("completed", status.ActivityStatus);
        Assert.Equal(fixture.Clock.GetUtcNow(), status.LastActivityAtUtc);
    }

    [Fact]
    public async Task Resampling_unchanged_content_does_not_spend_again_but_a_changed_source_can_schedule_later()
    {
        await using var fixture = new Fixture();
        await fixture.StartAsync();
        await fixture.ConfigureAsync(new(true, true, true, 3));
        await fixture.Coordinator.PulseAsync();
        var initialRevision = fixture.Snapshot.Revision;
        fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        await fixture.Host.SampleAsync(default);
        Assert.Equal(initialRevision, fixture.Snapshot.Revision);
        fixture.Coordinator.NotifyChanged("focus.completed");
        await fixture.Coordinator.PulseAsync();
        Assert.Equal(1, fixture.Model.Dispatches);

        fixture.ActivityContent = "A later focus interval has ended; task completion is unknown.";
        await fixture.Host.SampleAsync(default);
        Assert.NotEqual(initialRevision, fixture.Snapshot.Revision);
        Assert.DoesNotContain(fixture.Snapshot.Observations, item => item.IsInterpretation);
        await fixture.Coordinator.PulseAsync();
        Assert.Equal(2, fixture.Model.Dispatches);
        Assert.Equal(2, fixture.Host.GetStatus().RequestsUsed);
        Assert.Contains(fixture.Model.Requests[1].Configuration.Context.Observations, item => item.Content == fixture.ActivityContent);
    }

    [Fact]
    public async Task Foreground_work_defers_background_judgment_without_losing_the_initial_enable_event()
    {
        await using var fixture = new Fixture();
        await fixture.StartAsync();
        await fixture.ConfigureAsync(new(true, false, true));
        using (fixture.Lifetime.Inner.Acquire(default))
        {
            await fixture.Coordinator.PulseAsync();
            Assert.Empty(fixture.Model.Requests);
            Assert.Equal(0, fixture.Host.GetStatus().RequestsUsed);
        }
        await fixture.Coordinator.PulseAsync();
        Assert.Equal(1, fixture.Model.Dispatches);
    }

    [Theory]
    [InlineData("endpoint")]
    [InlineData("model")]
    [InlineData("capability")]
    public async Task Model_route_changes_revoke_approval_and_old_model_selection_does_not_restore_it(string change)
    {
        await using var fixture = new Fixture();
        await fixture.StartAsync();
        await fixture.ConfigureAsync(new(true, true, true));
        await fixture.AllowWindowAsync();
        var oldStatus = fixture.Host.GetStatus();
        var oldConfiguration = fixture.Host.Capture()!;
        var original = fixture.Connection;
        fixture.Connection = change switch
        {
            "endpoint" => new("synthetic", "https://another.invalid/v1", "test-model"),
            "model" => new("synthetic", "https://synthetic.invalid/v1", "another-model"),
            _ => original with { ToolsSupported = false }
        };
        var changed = fixture.Host.GetStatus();
        Assert.NotEqual(oldStatus.ContextToken, changed.ContextToken);
        Assert.False(changed.ModelApprovalCurrent);
        Assert.Empty(changed.Windows);
        Assert.False(fixture.Host.ApplicationsAuthorized(fixture.Scope));
        if (fixture.Host.GetSnapshot(fixture.Scope) is { } refreshed)
        {
            Assert.NotEqual(oldConfiguration.Context.Revision, refreshed.Revision);
            Assert.All(refreshed.Observations, item => Assert.Equal("focus.state", item.SourceId));
        }
        Assert.Null(fixture.Host.Capture());
        Assert.False(fixture.Host.TryCommit(oldConfiguration, () => throw new InvalidOperationException("must not commit")));
        Assert.False(fixture.Host.TryReserve(fixture.Reservation(oldConfiguration)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Host.ConfigureAsync(
            oldStatus.ContextToken, oldStatus.Revision, oldStatus.Options, default).AsTask());

        fixture.Connection = original;
        Assert.False(fixture.Host.GetStatus().ModelApprovalCurrent);
        Assert.Null(new CompanionAwarenessStore(fixture.DatabasePath).Read(CompanionAwarenessStore.ScopeKey(fixture.Scope)).ApprovedRoute);
        await fixture.Host.SampleAsync(default);
        Assert.Equal("focus.state", Assert.Single(fixture.Snapshot.Observations).SourceId);
    }

    [Theory]
    [InlineData("conversation")]
    [InlineData("role")]
    [InlineData("project")]
    public async Task Scope_changes_discard_old_snapshots_window_grants_and_pending_editor_tokens(string change)
    {
        await using var fixture = new Fixture();
        await fixture.StartAsync();
        await fixture.ConfigureAsync(new(true, true, true));
        await fixture.AllowWindowAsync();
        var oldScope = fixture.Scope;
        var oldStatus = fixture.Host.GetStatus();
        var captured = fixture.Host.Capture()!;
        fixture.Scope = change switch
        {
            "conversation" => oldScope with { ConversationId = "conversation-2" },
            "role" => oldScope with { RoleId = "other-role" },
            _ => oldScope with { ProjectId = "other-project" }
        };
        var status = fixture.Host.GetStatus();
        Assert.NotEqual(oldStatus.ContextToken, status.ContextToken);
        Assert.Empty(status.Windows);
        Assert.Null(fixture.Host.GetSnapshot(oldScope));
        Assert.Null(fixture.Host.GetSnapshot(fixture.Scope));
        Assert.False(fixture.Host.TryCommit(captured, () => throw new InvalidOperationException("must not commit")));
        Assert.False(fixture.Host.TryReserve(fixture.Reservation(captured)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Host.AllowWindowAsync(
            oldStatus.ContextToken, oldStatus.Revision, fixture.Windows.Candidate.Token, default).AsTask());
        await fixture.Host.SampleAsync(default);
        Assert.Equal(fixture.Scope, fixture.Snapshot.Scope);
        Assert.DoesNotContain(fixture.Snapshot.Observations, item => item.SourceId == "desktop.window");
        Assert.Equal(change == "conversation", status.ModelApprovalCurrent);
        Assert.Equal(change == "conversation", fixture.Host.ApplicationsAuthorized(fixture.Scope));
    }

    [Theory]
    [InlineData("model")]
    [InlineData("scope")]
    [InlineData("applications")]
    public async Task Withdrawal_rejects_a_dispatched_late_result_and_never_refunds_its_charge(string withdrawal)
    {
        await using var fixture = new Fixture();
        await fixture.StartAsync();
        await fixture.ConfigureAsync(new(true, true, true));
        fixture.Model.Hold = true;
        var pulse = fixture.Coordinator.PulseAsync();
        await fixture.Model.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task? configure = null;
        if (withdrawal == "applications") configure = fixture.ConfigureAsync(new(true, false, true));
        else
        {
            if (withdrawal == "model") fixture.Connection = new("synthetic", "https://synthetic.invalid/v1", "other-model");
            else fixture.Scope = fixture.Scope with { RoleId = "other-role" };
            fixture.Host.GetStatus();
        }
        await fixture.Model.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (configure is not null) Assert.False(configure.IsCompleted);
        fixture.Model.Release.TrySetResult(); // The synthetic provider deliberately returns despite cancellation.
        await pulse.WaitAsync(TimeSpan.FromSeconds(5));
        if (configure is not null) await configure.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Host.SampleAsync(default);
        Assert.DoesNotContain(fixture.Snapshot.Observations, item => item.IsInterpretation);
        Assert.Equal(1, fixture.Model.Dispatches);
        Assert.Equal(1, fixture.Host.GetStatus().RequestsUsed);
        Assert.False(fixture.Host.ApplicationsAuthorized(fixture.Scope));
    }

    [Fact]
    public async Task Enabling_during_an_old_source_read_keeps_the_new_baseline_event_and_discards_the_old_read()
    {
        await using var fixture = new Fixture();
        await fixture.StartAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.ReadActivity = async (_, _) =>
        {
            started.TrySetResult();
            await release.Task;
            return [fixture.Observation("focus.state", "obsolete source read")];
        };
        var oldSample = fixture.Host.SampleAsync(default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await fixture.ConfigureAsync(new(true, true, true), waitForSample: false);
            Assert.Null(fixture.Host.GetSnapshot(fixture.Scope));
        }
        finally { release.TrySetResult(); }
        await oldSample.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.ReadActivity = null;
        await fixture.Host.SampleAsync(default);
        await fixture.Coordinator.PulseAsync();
        Assert.Equal(1, fixture.Model.Dispatches);
        Assert.DoesNotContain(Assert.Single(fixture.Model.Requests).Configuration.Context.Observations,
            item => item.Content == "obsolete source read");
    }

    [Fact]
    public async Task Revoking_a_source_during_sampling_does_not_publish_its_late_content()
    {
        await using var fixture = new Fixture();
        await fixture.StartAsync();
        await fixture.ConfigureAsync(new(true, true, false));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.ReadActivity = async (_, _) =>
        {
            started.TrySetResult();
            await release.Task;
            return [fixture.Observation("focus.state", "late withdrawn activity")];
        };
        var oldSample = fixture.Host.SampleAsync(default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try { await fixture.ConfigureAsync(new(false, false, false), waitForSample: false); }
        finally { release.TrySetResult(); }
        await oldSample.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Host.SampleAsync(default);
        Assert.Empty(fixture.Snapshot.Observations);
        Assert.False(fixture.Host.ApplicationsAuthorized(fixture.Scope));
        Assert.Empty(fixture.Model.Requests);
    }

    [Fact]
    public async Task Windows_require_a_listed_token_and_reuse_is_rejected_after_clearing_the_grant()
    {
        await using var fixture = new Fixture();
        await fixture.StartAsync();
        await fixture.ConfigureAsync(new(false, false, false));
        var status = fixture.Host.GetStatus();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Host.AllowWindowAsync(
            status.ContextToken, status.Revision, fixture.Windows.Candidate.Token, default).AsTask());
        Assert.Empty(fixture.Windows.Reads);
        var listed = await fixture.Host.ListWindowsAsync(status.ContextToken, default);
        Assert.Equal(fixture.Windows.Candidate.Token, Assert.Single(listed).Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Host.AllowWindowAsync(
            status.ContextToken, status.Revision, "unlisted-token", default).AsTask());
        fixture.Windows.Text = new string('x', 1500);
        var allowed = await fixture.Host.AllowWindowAsync(status.ContextToken, status.Revision, listed[0].Token, default);
        await fixture.WaitForSnapshotAsync(snapshot => snapshot.Observations.Any(item => item.SourceId == "desktop.window"));
        Assert.Single(allowed.Windows);
        Assert.NotEmpty(fixture.Windows.Reads);
        Assert.All(fixture.Windows.Reads, token => Assert.Equal(listed[0].Token, token));
        var observation = Assert.Single(fixture.Snapshot.Observations);
        Assert.Equal("desktop.window", observation.SourceId);
        using (var data = JsonDocument.Parse(observation.Content))
        {
            Assert.Equal(1200, data.RootElement.GetProperty("visibleTextExcerpt").GetString()!.Length);
            Assert.True(data.RootElement.GetProperty("partial").GetBoolean());
        }

        var cleared = await fixture.Host.ClearWindowsAsync(allowed.ContextToken, allowed.Revision, default);
        await fixture.WaitForSnapshotAsync(snapshot => snapshot.Observations.Count == 0);
        Assert.Empty(cleared.Windows);
        Assert.Empty(fixture.Snapshot.Observations);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Host.AllowWindowAsync(
            cleared.ContextToken, cleared.Revision, listed[0].Token, default).AsTask());
        Assert.All(fixture.Windows.Reads, token => Assert.Equal(listed[0].Token, token));
    }

    [Theory]
    [InlineData("title")]
    [InlineData("application")]
    public async Task A_successful_window_read_with_different_source_identity_is_never_projected(string changed)
    {
        await using var fixture = new Fixture();
        await fixture.StartAsync();
        await fixture.ConfigureAsync(new(false, false, false));
        await fixture.AllowWindowAsync();
        Assert.Single(fixture.Snapshot.Observations);
        fixture.Windows.Read = (_, _) => Task.FromResult(new WindowTextSnapshot(
            changed == "title" ? "Different window" : fixture.Windows.Candidate.Title,
            changed == "application" ? "Different app" : fixture.Windows.Candidate.Application,
            "content outside selected identity", fixture.Clock.GetUtcNow(), WindowTextReadStatus.Complete));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        do
        {
            await fixture.Host.SampleAsync(timeout.Token);
            if (fixture.Host.GetStatus().Windows.Count == 0) break;
            await Task.Delay(1, timeout.Token);
        } while (true);
        Assert.Empty(fixture.Host.GetStatus().Windows);
        Assert.Empty(fixture.Snapshot.Observations);
    }

    [Fact]
    public async Task Window_text_expires_without_resampling_and_unavailable_tokens_revoke_the_window()
    {
        await using var fixture = new Fixture();
        await fixture.StartAsync();
        await fixture.ConfigureAsync(new(false, false, false));
        var readAt = fixture.Clock.GetUtcNow();
        fixture.Windows.Read = (_, _) => Task.FromResult(new WindowTextSnapshot(
            fixture.Windows.Candidate.Title, fixture.Windows.Candidate.Application,
            fixture.Windows.Text, readAt, WindowTextReadStatus.Complete));
        await fixture.AllowWindowAsync();
        Assert.Single(fixture.Snapshot.Observations);
        fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Empty(fixture.Snapshot.Observations);
        Assert.NotEmpty(fixture.Windows.Reads);
        fixture.Windows.Read = (_, _) => Task.FromResult(new WindowTextSnapshot(
            "", "", "", fixture.Clock.GetUtcNow(), WindowTextReadStatus.Unavailable));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        do
        {
            await fixture.Host.SampleAsync(timeout.Token);
            if (fixture.Host.GetStatus().Windows.Count == 0) break;
            await Task.Delay(1, timeout.Token);
        } while (true);
        Assert.Empty(fixture.Host.GetStatus().Windows);
        Assert.Empty(fixture.Snapshot.Observations);
    }

    [Fact]
    public async Task Window_enumeration_cannot_return_candidates_across_a_scope_change()
    {
        await using var fixture = new Fixture();
        await fixture.StartAsync();
        await fixture.ConfigureAsync(new(false, false, false));
        var token = fixture.Host.GetStatus().ContextToken;
        fixture.Windows.OnList = () => fixture.Scope = fixture.Scope with { RoleId = "other-role" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Host.ListWindowsAsync(token, default).AsTask());
        Assert.Empty(fixture.Host.GetStatus().Windows);
        Assert.Empty(fixture.Windows.Reads);
    }

    [Fact]
    public async Task A_failing_source_removes_previous_facts_and_the_interpretation_that_depended_on_them()
    {
        await using var fixture = new Fixture();
        await fixture.StartAsync();
        await fixture.ConfigureAsync(new(true, true, true));
        await fixture.Coordinator.PulseAsync();
        Assert.Contains(fixture.Snapshot.Observations, item => item.IsInterpretation);
        fixture.ReadApplications = _ => throw new InvalidOperationException("synthetic source failure");
        await fixture.Host.SampleAsync(default);
        Assert.Equal("focus.state", Assert.Single(fixture.Snapshot.Observations).SourceId);
        Assert.DoesNotContain(fixture.Host.Capture()!.Context.Observations, item => item.SourceId == "desktop.apps");
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("corrupt")]
    public async Task Storage_failure_closes_private_reading_projection_and_background_dispatch(string failure)
    {
        await using var fixture = new Fixture();
        await fixture.StartAsync();
        await fixture.ConfigureAsync(new(true, true, true));
        await fixture.AllowWindowAsync();
        var reads = fixture.ApplicationReads;
        var windowReads = fixture.Windows.Reads.Count;
        if (failure == "delete") File.Delete(fixture.DatabasePath);
        else File.WriteAllText(fixture.DatabasePath, "synthetic damaged database");

        var status = fixture.Host.GetStatus();
        Assert.False(status.Available);
        Assert.False(status.ModelApprovalCurrent);
        Assert.False(fixture.Host.ApplicationsAuthorized(fixture.Scope));
        Assert.Null(fixture.Host.GetSnapshot(fixture.Scope));
        Assert.Null(fixture.Host.Capture());
        await fixture.Host.SampleAsync(default);
        fixture.Coordinator.NotifyChanged();
        await fixture.Coordinator.PulseAsync();
        Assert.Equal(reads, fixture.ApplicationReads);
        Assert.Equal(windowReads, fixture.Windows.Reads.Count);
        Assert.Empty(fixture.Model.Requests);
        Assert.Equal(-1, fixture.Host.GetStatus().RequestsUsed);
    }

    [Theory]
    [InlineData("snapshot")]
    [InlineData("applications")]
    [InlineData("capture")]
    public async Task Corrupt_storage_closes_private_context_even_when_status_has_not_been_polled(string firstEntry)
    {
        await using var fixture = new Fixture();
        await fixture.StartAsync();
        await fixture.ConfigureAsync(new(true, true, true));
        File.WriteAllText(fixture.DatabasePath, "synthetic damaged database");
        if (firstEntry == "snapshot") Assert.Null(fixture.Host.GetSnapshot(fixture.Scope));
        else if (firstEntry == "applications") Assert.False(fixture.Host.ApplicationsAuthorized(fixture.Scope));
        else Assert.Null(fixture.Host.Capture());
        Assert.False(fixture.Host.GetStatus().Available);
    }

    [Fact]
    public async Task Owner_events_immediately_invalidate_old_facts_and_coalesce_a_read_in_flight_into_fresh_context()
    {
        await using var fixture = new Fixture();
        await fixture.StartAsync();
        await fixture.ConfigureAsync(new(true, true, true));
        await fixture.Coordinator.PulseAsync();
        var previous = fixture.Host.Capture()!;
        fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        fixture.ReadActivity = async (_, _) =>
        {
            if (Interlocked.Increment(ref reads) == 1)
            {
                started.TrySetResult();
                await release.Task;
                return [fixture.Observation("focus.state", "obsolete in-flight source")];
            }
            return [fixture.Observation("focus.state", "newest owner state")];
        };
        fixture.Host.ContextChanged();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.Null(fixture.Host.GetSnapshot(fixture.Scope));
            Assert.False(fixture.Host.TryCommit(previous, () => throw new InvalidOperationException("must not commit")));
            fixture.Host.ContextChanged();
            fixture.Host.ContextChanged();
        }
        finally { release.TrySetResult(); }
        await fixture.WaitForSnapshotAsync(snapshot => snapshot.Observations.Any(item => item.Content == "newest owner state"));
        Assert.DoesNotContain(fixture.Snapshot.Observations, item => item.IsInterpretation || item.Content == "obsolete in-flight source");
        await fixture.Coordinator.PulseAsync();
        Assert.Equal(2, fixture.Model.Dispatches);
        Assert.Contains(fixture.Model.Requests[1].Configuration.Context.Observations, item => item.Content == "newest owner state");
        Assert.DoesNotContain(fixture.Model.Requests[1].Configuration.Context.Observations, item => item.Content == "obsolete in-flight source");
    }

    [Fact]
    public async Task An_owner_event_with_unchanged_semantic_facts_does_not_spend_again_after_the_cooldown()
    {
        await using var fixture = new Fixture();
        await fixture.StartAsync();
        await fixture.ConfigureAsync(new(true, true, true));
        await fixture.Coordinator.PulseAsync();
        var revision = fixture.Snapshot.Revision;
        fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        fixture.Host.ContextChanged();
        await fixture.WaitForSnapshotAsync(snapshot => snapshot.Observations.Count > 0
            && snapshot.Observations.All(item => item.ObservedAtUtc == fixture.Clock.GetUtcNow()));
        Assert.Equal(revision, fixture.Snapshot.Revision);
        await fixture.Coordinator.PulseAsync();
        Assert.Equal(1, fixture.Model.Dispatches);
        Assert.Equal(1, fixture.Host.GetStatus().RequestsUsed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Forgetting_removes_source_approval_and_notes_but_does_not_reset_spent_quota(bool all)
    {
        await using var fixture = new Fixture();
        await fixture.StartAsync();
        await fixture.ConfigureAsync(new(true, true, true, 1));
        await fixture.AllowWindowAsync();
        await fixture.Coordinator.PulseAsync();
        Assert.Contains(fixture.Snapshot.Observations, item => item.IsInterpretation);
        if (all) fixture.Host.ForgetAll();
        else fixture.Host.ForgetConversation(fixture.Scope.ConversationId, fixture.Scope.RoleId);
        var status = fixture.Host.GetStatus();
        Assert.False(status.ModelApprovalCurrent);
        Assert.Equal(new CompanionAwarenessOptions(), status.Options);
        Assert.Empty(status.Windows);
        Assert.Equal(1, status.RequestsUsed);
        Assert.All(fixture.Host.GetSnapshot(fixture.Scope)?.Observations ?? [],
            item => Assert.Equal("focus.state", item.SourceId));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        do
        {
            await fixture.Host.SampleAsync(timeout.Token);
            if (fixture.Host.GetSnapshot(fixture.Scope) is not null) break;
            await Task.Delay(1, timeout.Token);
        } while (true);
        Assert.Equal("focus.state", Assert.Single(fixture.Snapshot.Observations).SourceId);
        Assert.Equal(1, new CompanionAwarenessStore(fixture.DatabasePath).Used(fixture.Clock.GetUtcNow()));
    }

    [Fact]
    public async Task Restart_and_role_change_keep_spent_quota_but_do_not_restore_a_window_grant()
    {
        await using var first = new Fixture();
        await first.StartAsync();
        await first.ConfigureAsync(new(true, true, true, 1));
        await first.AllowWindowAsync();
        await first.Coordinator.PulseAsync();
        await first.Host.DisposeAsync();

        await using var restarted = new Fixture(first.DatabasePath);
        await restarted.StartAsync();
        Assert.True(restarted.Host.GetStatus().ModelApprovalCurrent);
        Assert.Empty(restarted.Host.GetStatus().Windows);
        Assert.Empty(restarted.Windows.Reads);
        Assert.DoesNotContain(restarted.Snapshot.Observations, item => item.SourceId == "desktop.window");
        restarted.Scope = restarted.Scope with { RoleId = "other-role" };
        await restarted.ConfigureAsync(new(true, false, true, 1));
        await restarted.Coordinator.PulseAsync();
        Assert.Single(restarted.Model.Requests);
        Assert.Equal(0, restarted.Model.Dispatches);
        Assert.Equal(1, restarted.Host.GetStatus().RequestsUsed);
        Assert.Equal("COMPANION_ACTIVITY_BUDGET_UNAVAILABLE", restarted.Coordinator.LastFailureCode);
    }

    [Fact]
    public async Task Restore_reset_revokes_live_context_and_saved_permissions_without_resetting_spent_quota()
    {
        await using var fixture = new Fixture();
        await fixture.StartAsync();
        await fixture.ConfigureAsync(new(true, true, true, 1));
        await fixture.AllowWindowAsync();
        await fixture.Coordinator.PulseAsync();
        var before = fixture.Host.Capture()!;
        Assert.Contains(fixture.Snapshot.Observations, item => item.IsInterpretation);

        Assert.True(fixture.Host.ResetAfterRestore());
        Assert.False(fixture.Host.GetStatus().ModelApprovalCurrent);
        Assert.Empty(fixture.Host.GetStatus().Windows);
        Assert.Null(fixture.Host.GetSnapshot(fixture.Scope));
        Assert.False(fixture.Host.ApplicationsAuthorized(fixture.Scope));
        Assert.False(fixture.Host.TryCommit(before, () => throw new InvalidOperationException("must not commit")));
        var reopened = new CompanionAwarenessStore(fixture.DatabasePath);
        Assert.Null(reopened.Read(CompanionAwarenessStore.ScopeKey(fixture.Scope)).ApprovedRoute);
        Assert.Equal(1, reopened.Used(fixture.Clock.GetUtcNow()));
        Assert.False(reopened.TryReserve(fixture.Reservation(before), 1));
        Assert.True(File.Exists(fixture.DatabasePath + ".permissions-revoked"));
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("corrupt")]
    public async Task Restore_reset_with_unavailable_storage_does_not_throw_and_keeps_a_durable_revocation_fence(string failure)
    {
        await using var fixture = new Fixture();
        await fixture.StartAsync();
        await fixture.ConfigureAsync(new(true, true, false));
        await fixture.AllowWindowAsync();
        if (failure == "delete") File.Delete(fixture.DatabasePath);
        else File.WriteAllText(fixture.DatabasePath, "synthetic damaged database");

        Assert.True(fixture.Host.ResetAfterRestore());
        var marker = fixture.DatabasePath + ".permissions-revoked";
        Assert.True(File.Exists(marker));
        Assert.False(fixture.Host.GetStatus().Available);
        Assert.False(fixture.Host.GetStatus().ModelApprovalCurrent);
        Assert.Empty(fixture.Host.GetStatus().Windows);
        Assert.False(fixture.Host.ApplicationsAuthorized(fixture.Scope));
        Assert.Null(fixture.Host.GetSnapshot(fixture.Scope));
        Assert.Null(fixture.Host.Capture());
        var reopened = new CompanionAwarenessStore(fixture.DatabasePath);
        Assert.False(reopened.Available);
        Assert.Equal(-1, reopened.Used(fixture.Clock.GetUtcNow()));
        Assert.True(File.Exists(marker));
        if (failure == "delete") Assert.False(File.Exists(fixture.DatabasePath));
        else Assert.Equal("synthetic damaged database", File.ReadAllText(fixture.DatabasePath));
    }

    [Theory]
    [InlineData("configure")]
    [InlineData("allowWindow")]
    [InlineData("clearWindows")]
    public async Task Cancellation_after_suspension_cannot_mutate_saved_options_or_window_authorization(string operation)
    {
        await using var fixture = new Fixture();
        await fixture.StartAsync();
        await fixture.ConfigureAsync(new(true, false, false));
        if (operation == "clearWindows") await fixture.AllowWindowAsync();
        else if (operation == "allowWindow") await fixture.Host.ListWindowsAsync(fixture.Host.GetStatus().ContextToken, default);
        var before = fixture.Host.GetStatus();
        var policy = new CompanionAwarenessStore(fixture.DatabasePath).Read(CompanionAwarenessStore.ScopeKey(fixture.Scope));
        using var cancellation = new CancellationTokenSource();
        fixture.Lifetime.CancelAfterSuspension = cancellation;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation switch
        {
            "configure" => fixture.Host.ConfigureAsync(before.ContextToken, before.Revision, new(false, true, true), cancellation.Token).AsTask(),
            "allowWindow" => fixture.Host.AllowWindowAsync(before.ContextToken, before.Revision, fixture.Windows.Candidate.Token, cancellation.Token).AsTask(),
            _ => fixture.Host.ClearWindowsAsync(before.ContextToken, before.Revision, cancellation.Token).AsTask()
        });
        var after = fixture.Host.GetStatus();
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(before.Options, after.Options);
        Assert.Equal(before.Windows, after.Windows);
        Assert.Equal(policy, new CompanionAwarenessStore(fixture.DatabasePath).Read(CompanionAwarenessStore.ScopeKey(fixture.Scope)));
    }

    [Fact]
    public async Task Disposing_the_host_clears_projection_authorization_and_owned_window_reader()
    {
        await using var fixture = new Fixture();
        await fixture.StartAsync();
        await fixture.ConfigureAsync(new(true, true, true));
        await fixture.AllowWindowAsync();
        var before = fixture.Host.GetStatus();
        await fixture.Host.DisposeAsync();
        Assert.False(fixture.Host.GetStatus().Available);
        Assert.False(fixture.Host.ApplicationsAuthorized(fixture.Scope));
        Assert.Null(fixture.Host.GetSnapshot(fixture.Scope));
        Assert.Null(fixture.Host.Capture());
        Assert.True(fixture.Windows.Disposed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Host.ConfigureAsync(
            before.ContextToken, before.Revision, before.Options, default).AsTask());
        await fixture.Coordinator.PulseAsync();
        Assert.Empty(fixture.Model.Requests);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string? _ownedRoot;
        private int _activityReads;
        private int _applicationReads;
        public Fixture(string? databasePath = null)
        {
            if (databasePath is null) _ownedRoot = Path.Combine(Path.GetTempPath(), "fgo-awareness-host-" + Guid.NewGuid().ToString("N"));
            DatabasePath = databasePath ?? Path.Combine(_ownedRoot!, "awareness.db");
            Windows = new(Clock);
            Host = new(new(DatabasePath), () => Scope, () => Connection, ReadActivityCore, ReadApplicationsCore,
                Windows, Lifetime, Clock);
            Coordinator = new(Host, Model, Host, Lifetime, Clock, Host.Record);
        }
        public string DatabasePath { get; }
        public ToolScope Scope { get; set; } = new("conversation-1", "mash", "project-1");
        public ModelConnectionSettings Connection { get; set; } = new("synthetic", "https://synthetic.invalid/v1", "test-model");
        public string ActivityContent { get; set; } = "A focus interval ended; task completion is unknown.";
        public Func<ToolScope, CancellationToken, Task<IReadOnlyList<CompanionObservation>>>? ReadActivity { get; set; }
        public Func<CancellationToken, CompanionObservation>? ReadApplications { get; set; }
        public int ActivityReads => Volatile.Read(ref _activityReads);
        public int ApplicationReads => Volatile.Read(ref _applicationReads);
        public PassiveClock Clock { get; } = new();
        public SuspensionLifetime Lifetime { get; } = new();
        public SyntheticWindows Windows { get; }
        public SyntheticModel Model { get; } = new();
        public CompanionAwarenessHost Host { get; }
        public CompanionActivityCoordinator Coordinator { get; }
        public CompanionContextSnapshot Snapshot => Assert.IsType<CompanionContextSnapshot>(Host.GetSnapshot(Scope));

        public async Task StartAsync()
        {
            Host.Start(default);
            // Start performs one real sample. Attach afterwards so subsequent background work is driven
            // deterministically by PulseAsync, with the production admission/dispatch/commit path intact.
            await WaitUntilAsync(() => Host.GetSnapshot(Scope) is not null);
            Host.Attach(Coordinator);
        }

        public async Task ConfigureAsync(CompanionAwarenessOptions options, bool waitForSample = true)
        {
            var status = Host.GetStatus();
            await Host.ConfigureAsync(status.ContextToken, status.Revision, options, default);
            if (waitForSample) await WaitUntilAsync(() => Host.GetSnapshot(Scope) is not null);
        }

        public async Task AllowWindowAsync()
        {
            var status = Host.GetStatus();
            var windows = await Host.ListWindowsAsync(status.ContextToken, default);
            await Host.AllowWindowAsync(status.ContextToken, status.Revision, Assert.Single(windows).Token, default);
            await WaitUntilAsync(() => Host.GetSnapshot(Scope)?.Observations.Any(item => item.SourceId == "desktop.window") == true);
        }

        public CompanionObservation Observation(string source, string text)
            => CompanionAwarenessHost.Observe(source, text, Clock.GetUtcNow(), TimeSpan.FromMinutes(2));

        public CompanionActivityReservation Reservation(CompanionActivityConfiguration configuration)
            => new("stale-reservation", configuration.Context.Scope, ModelRouteKey.From(configuration.Connection),
                configuration.PolicyRevision, Clock.GetUtcNow(), 4096, 384);

        public Task WaitForSnapshotAsync(Func<CompanionContextSnapshot, bool> condition)
            => WaitUntilAsync(() => Host.GetSnapshot(Scope) is { } snapshot && condition(snapshot));

        private Task<IReadOnlyList<CompanionObservation>> ReadActivityCore(ToolScope scope, CancellationToken token)
        {
            Interlocked.Increment(ref _activityReads);
            return ReadActivity?.Invoke(scope, token)
                ?? Task.FromResult<IReadOnlyList<CompanionObservation>>([Observation("focus.state", ActivityContent)]);
        }

        private CompanionObservation ReadApplicationsCore(CancellationToken token)
        {
            Interlocked.Increment(ref _applicationReads);
            return ReadApplications?.Invoke(token) ?? Observation("desktop.apps", "Synthetic editor is running; foreground activity is unknown.");
        }

        public async ValueTask DisposeAsync()
        {
            Model.Release.TrySetResult();
            await Host.DisposeAsync();
            if (_ownedRoot is not null && Directory.Exists(_ownedRoot)) Directory.Delete(_ownedRoot, recursive: true);
        }

        private static async Task WaitUntilAsync(Func<bool> condition)
        {
            var timeout = DateTime.UtcNow.AddSeconds(5);
            while (!condition())
            {
                Assert.True(DateTime.UtcNow < timeout, "The real host did not finish its pending sample.");
                await Task.Delay(1);
            }
        }
    }

    private sealed class SyntheticModel : ICompanionActivityModel
    {
        public List<CompanionActivityRequest> Requests { get; } = [];
        public int Dispatches { get; private set; }
        public bool Hold { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<CompanionActivityJudgment> EvaluateAsync(CompanionActivityRequest request,
            IChatDispatchGate dispatchGate, Action<CancellationToken> ensureCurrent, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            using var cancellation = cancellationToken.Register(() => Cancelled.TrySetResult());
            ensureCurrent(cancellationToken);
            var scope = request.Configuration.Context.Scope;
            var chat = ChatRequest.CreateAgent(scope.RoleId, scope.ConversationId,
                [new(ModelMessageRole.System, "Synthetic bounded background judgment.", [])],
                metadata: new Dictionary<string, string>
                {
                    ["fgo_auxiliary"] = "companion_activity", ["fgo_activity_id"] = request.ActivityId
                }, maxOutputTokens: 384);
            dispatchGate.Dispatch(chat, ModelRouteKey.From(request.Configuration.Connection), () =>
            {
                Dispatches++;
                Started.TrySetResult();
                return true;
            }, cancellationToken);
            if (Hold) await Release.Task; // Deliberately ignore cancellation to exercise the host's final fence.
            return new("The interval may be relevant to later conversation; completed work is unknown.",
                request.Configuration.Context.Observations.Select(item => item.SourceId).ToArray());
        }
    }

    private sealed class SyntheticWindows(PassiveClock clock) : IWindowTextReader, IDisposable
    {
        public WindowTextCandidate Candidate { get; } = new("synthetic-window-token", "Synthetic document", "Synthetic editor");
        public string Text { get; set; } = "Visible synthetic window text.";
        public List<string> Reads { get; } = [];
        public int ListCalls { get; private set; }
        public Action? OnList { get; set; }
        public Func<string, CancellationToken, Task<WindowTextSnapshot>>? Read { get; set; }
        public bool Disposed { get; private set; }
        public IReadOnlyList<WindowTextCandidate> ListCandidates()
        {
            ListCalls++;
            OnList?.Invoke();
            return [Candidate];
        }
        public Task<WindowTextSnapshot> ReadAsync(string candidateToken, CancellationToken cancellationToken)
        {
            Reads.Add(candidateToken);
            return Read?.Invoke(candidateToken, cancellationToken) ?? Task.FromResult(new WindowTextSnapshot(
                Candidate.Title, Candidate.Application, Text, clock.GetUtcNow(), WindowTextReadStatus.Complete));
        }
        public void Dispose() => Disposed = true;
    }

    private sealed class SuspensionLifetime : IDialogueContextLifetime
    {
        public DialogueContextLifetime Inner { get; } = new();
        public CancellationTokenSource? CancelAfterSuspension { get; set; }
        public long Generation => Inner.Generation;
        public Task InvalidateAsync(CancellationToken cancellationToken) => Inner.InvalidateAsync(cancellationToken);
        public IDialogueBackgroundLease? TryAcquireBackground(CancellationToken cancellationToken) => Inner.TryAcquireBackground(cancellationToken);
        public async Task<IDisposable> SuspendAsync(CancellationToken cancellationToken)
        {
            var suspension = await Inner.SuspendAsync(cancellationToken);
            CancelAfterSuspension?.Cancel();
            return suspension;
        }
    }

    // Advance source freshness/cooldown without starting model timeout callbacks. Owner wake sampling and
    // the real coordinator are exercised; these tests do not cover periodic timer or timeout implementation.
    private sealed class PassiveClock : TimeProvider
    {
        private readonly DateTimeOffset _origin = new(2026, 10, 24, 10, 0, 0, TimeSpan.Zero);
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() => _origin.AddTicks(Interlocked.Read(ref _ticks));
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public void Advance(TimeSpan interval) => Interlocked.Add(ref _ticks, interval.Ticks);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            return new PassiveTimer();
        }
        private sealed class PassiveTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
