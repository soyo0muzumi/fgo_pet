using FgoPet.Core.Dialogue;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Understanding;
using FgoPet.Understanding.Contracts;
using Xunit;

namespace FgoPet.Plugin.Understanding.Tests;

public sealed class UnderstandingPolicyTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "fgopet-understanding-" + Guid.NewGuid().ToString("N"));
    private readonly ConversationScope _scope = new("mash", "project-a");
    private readonly Clock _clock = new();
    private readonly Sources _sources = new();
    private readonly RuntimeDatabase _database;
    private readonly UnderstandingService _service;
    public UnderstandingPolicyTests()
    {
        _database = new(Path.Combine(_directory, "runtime.db"), pooling: false);
        new RuntimeDatabaseMigrator(_database).Migrate();
        _service = new(_database, _sources, _clock, new AuditedFixtures());
    }
    private ConversationSourceSnapshot Root(string id = "original", ConversationSourceKind kind = ConversationSourceKind.User) =>
        new(new("conversation", "mash", "project-a"), id, new('A', 64), _clock.Now, kind, "Synthetic project");
    private UnderstandingProposal Proposal(string id = "interest", UnderstandingDomain domain = UnderstandingDomain.Self,
        UnderstandingKind kind = UnderstandingKind.CurrentInterest, ConversationSourceSnapshot? root = null) =>
        new(id, domain, kind, domain == UnderstandingDomain.Self ? "assistant" : "user", "最近想继续研究实际做过的绘图实验", "仅当前项目，暂定", [root ?? Root()]);
    private void Configure(UnderstandingMode mode = UnderstandingMode.Bounded) => _service.Configure(_scope,
        _service.State(_scope).Revision, new(mode, ReadSources: true, SaveCandidates: true, UseInDialogue: true), 42);
    private UnderstandingReceipt Apply(UnderstandingProposal proposal, string operation = "review-1", bool manual = false) =>
        _service.Apply(_service.Begin(_scope, operation, proposal.Sources, manual,
            coverage: proposal.Sources.Select(s => new UnderstandingSourceCoverage(s, 0, 4, 4)).ToArray()), [proposal]);

    [Fact] public void Off_manual_preview_does_not_persist_body_and_requires_exact_explicit_save()
    {
        Assert.Equal(UnderstandingMode.Off, _service.State(_scope).Policy.Mode);
        _service.Configure(_scope, 0, new(ReadSources: true));
        Assert.Throws<InvalidOperationException>(() => _service.Begin(_scope, "auto", [Root()]));
        Assert.Equal(UnderstandingOutcome.ReviewedNotPersisted, Assert.Single(Apply(Proposal(), manual: true).Items).Outcome);
        Assert.Empty(_service.Query(_scope, "").Items);
        var reopened = new UnderstandingService(_database, _sources, _clock);
        Assert.Empty(reopened.List(_scope, true));
        Assert.Empty(reopened.List(_scope, false));
        var saved = _service.Review(_scope, "save", "interest", 1, UnderstandingAction.Approve);
        Assert.Equal(UnderstandingOutcome.Applied, Assert.Single(saved.Items).Outcome);
        Assert.Single(_service.List(_scope, false));
    }

    [Fact] public void High_impact_pending_isolated_until_exact_version_approval_and_idempotent_retry()
    {
        Configure();
        var proposal = Proposal("user-interpretation", UnderstandingDomain.User, UnderstandingKind.Interpretation);
        Assert.Equal(UnderstandingOutcome.ProposedPending, Assert.Single(Apply(proposal).Items).Outcome);
        Assert.Empty(_service.Query(_scope, "").Items);
        Assert.Equal("UNDERSTANDING_STALE", Assert.Single(_service.Review(_scope, "wrong", proposal.Id, 9, UnderstandingAction.Approve).Items).Code);
        var receipt = _service.Review(_scope, "approve", proposal.Id, 1, UnderstandingAction.Approve);
        Assert.Single(_service.Query(_scope, "").Items);
        _service.Review(_scope, "correct", proposal.Id, 2, UnderstandingAction.Correct, "较新的明确纠正");
        Assert.Equal(receipt.Items, _service.Review(_scope, "approve", proposal.Id, 1, UnderstandingAction.Approve).Items);
        var entry = Assert.Single(_service.Query(_scope, "").Items);
        Assert.Equal("较新的明确纠正", entry.Correction);
        Assert.Equal("model_inferred", entry.BasisKind);
    }

    [Fact] public void Bounded_expiry_and_downgrade_never_resurrect_on_mode_upgrade_or_historical_read()
    {
        Configure();
        Assert.Equal(UnderstandingOutcome.Applied, Assert.Single(Apply(Proposal()).Items).Outcome);
        Assert.Single(_service.Query(_scope, "").Items);
        _clock.Now = _clock.Now.AddDays(8);
        Assert.Empty(_service.Query(_scope, "").Items);
        var historical = Assert.Single(_service.Query(_scope, "", historical: true, entryId: "interest").Items);
        Assert.Equal(UnderstandingLife.Dormant, historical.Life);
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.Zero), historical.ActiveUntil);
        Configure(UnderstandingMode.Off);
        Configure();
        Assert.Empty(_service.Query(_scope, "").Items);
    }

    [Fact] public void Stale_batch_cannot_overwrite_correction_or_escape_via_add_and_failed_dependency_is_atomic()
    {
        Configure(); Apply(Proposal());
        var old = _service.Begin(_scope, "old", [Root()]);
        _service.Review(_scope, "correction", "interest", 1, UnderstandingAction.Correct, "new correction");
        Assert.Equal("UNDERSTANDING_STALE", Assert.Single(_service.Apply(old, [Proposal("new-id")]).Items).Code);
        var fresh = _service.Begin(_scope, "dependency", [Root()]);
        var bad = Proposal("replacement") with { TargetId = "interest", TargetRevision = 1 };
        Assert.All(_service.Apply(fresh, [Proposal("should-not-appear"), bad]).Items, item => Assert.Equal(UnderstandingOutcome.Failed, item.Outcome));
        Assert.DoesNotContain(_service.List(_scope, false), item => item.Id == "should-not-appear");
    }

    [Fact] public void Revoked_changed_or_wrong_scope_sources_never_publish_or_approve()
    {
        Configure();
        var ticket = _service.Begin(_scope, "source-change", [Root()]);
        _sources.Current = false;
        Assert.Equal("UNDERSTANDING_SOURCE_CHANGED", Assert.Single(_service.Apply(ticket, [Proposal()]).Items).Code);
        Assert.Empty(_service.List(_scope, false));
        _sources.Current = true;
        var wrong = Root() with { Scope = new("conversation", "other-role", "project-a") };
        Assert.Throws<InvalidOperationException>(() => _service.Begin(_scope, "wrong", [wrong]));
    }

    [Fact] public void Self_reported_low_risk_does_not_bypass_host_policy_and_user_facts_stay_with_memory()
    {
        Configure();
        Assert.Equal(UnderstandingOutcome.ProposedPending, Assert.Single(Apply(Proposal("not-audited")).Items).Outcome);
        Assert.Equal("UNDERSTANDING_MEMORY_OWNER_REQUIRED", Assert.Single(Apply(Proposal("fact", UnderstandingDomain.User, UnderstandingKind.UserFact), "fact-op").Items).Code);
        Assert.Equal("UNDERSTANDING_DEFINITION_FLOW_REQUIRED", Assert.Single(Apply(Proposal("identity", kind: UnderstandingKind.StableIdentity), "identity-op").Items).Code);
    }

    [Fact] public void Source_commit_during_validation_and_cancellation_before_commit_leave_no_entry()
    {
        Configure(); var ticket = _service.Begin(_scope, "race", [Root()]);
        _sources.OnCheck = () =>
        {
            _sources.OnCheck = null;
            using var db = _database.Open(); using var cmd = db.CreateCommand();
            cmd.CommandText = "CREATE TABLE synthetic_source_mutation(id INTEGER)"; cmd.ExecuteNonQuery();
        };
        Assert.Equal("UNDERSTANDING_STALE", Assert.Single(_service.Apply(ticket, [Proposal()]).Items).Code);
        var cancelled = _service.Begin(_scope, "cancelled", [Root()]); using var cts = new CancellationTokenSource();
        _sources.OnCheck = cts.Cancel;
        Assert.Throws<OperationCanceledException>(() => _service.Apply(cancelled, [Proposal()], cts.Token));
        Assert.Empty(_service.List(_scope, false)); Assert.Empty(_service.List(_scope, true));
    }

    [Fact] public void Privacy_forget_purges_all_revisions_and_old_backup_cannot_restore_deleted_entry_or_grants()
    {
        Configure(); Apply(Proposal());
        var backup = new RuntimeDatabase(Path.Combine(_directory, "backup.db"), pooling: false);
        using (var source = _database.Open()) using (var target = backup.Open()) source.BackupDatabase(target);
        _service.Review(_scope, "delete", "interest", 1, UnderstandingAction.Delete);
        _service.NormalizeRestoredCopy(backup.DatabasePath);
        var restored = new UnderstandingService(backup, _sources, _clock);
        Assert.Empty(restored.List(_scope, false)); Assert.Empty(restored.List(_scope, true));
        Assert.Equal(new UnderstandingPolicy(), restored.State(_scope).Policy);
        _service.ForgetAll();
        using var db = _database.Open(); using var command = db.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM understanding_revisions";
        Assert.Equal(0L, command.ExecuteScalar());
    }

    [Fact] public void Off_manual_attempt_metadata_survives_crash_without_body_or_automatic_resend()
    {
        _service.Configure(_scope, 0, new(ReadSources: true, ExternalProcessing: true));
        Assert.True(_service.TryReserveAttempt(_scope, "manual-attempt", [Root()], manual: true));
        Assert.False(_service.TryReserveAttempt(_scope, "manual-attempt", [Root()], manual: true));
        var restarted = new UnderstandingService(_database, _sources, _clock); restarted.StartSession();
        Assert.Equal(("Interrupted", 1), Assert.Single(restarted.AttemptStates(_scope)));
        Assert.False(restarted.TryReserveAttempt(_scope, "manual-attempt", [Root()], manual: true));
        Assert.Empty(restarted.List(_scope, true)); Assert.Empty(restarted.List(_scope, false));
    }

    [Fact] public void Pending_replacement_cannot_retire_current_entry_and_approval_rechecks_target_revision()
    {
        Configure(); Apply(Proposal());
        var proposal = Proposal("replacement", UnderstandingDomain.User, UnderstandingKind.Interpretation) with { TargetId = "interest", TargetRevision = 1 };
        Assert.Equal(UnderstandingOutcome.ProposedPending, Assert.Single(Apply(proposal, "replace").Items).Outcome);
        Assert.Equal("interest", Assert.Single(_service.Query(_scope, "").Items).Id);
        _service.Review(_scope, "correct-old", "interest", 1, UnderstandingAction.Correct, "newer correction");
        Assert.Equal("UNDERSTANDING_STALE", Assert.Single(_service.Review(_scope, "approve-replacement", "replacement", 1, UnderstandingAction.Approve).Items).Code);
        Assert.Equal("newer correction", Assert.Single(_service.Query(_scope, "").Items).Correction);
    }
    [Fact] public void A_self_label_cannot_automatically_replace_an_approved_user_interpretation()
    {
        Configure();var user=Proposal("approved-user",UnderstandingDomain.User,UnderstandingKind.Interpretation);
        Apply(user,"create-user");_service.Review(_scope,"approve-user",user.Id,1,UnderstandingAction.Approve);
        var rewrite=Proposal("interest",root:Root("new-original"))with{TargetId=user.Id,TargetRevision=2};
        var receipt=Apply(rewrite,"cross-domain-rewrite");
        Assert.Equal(UnderstandingOutcome.ProposedPending,Assert.Single(receipt.Items).Outcome);
        Assert.Equal(user.Id,Assert.Single(_service.Query(_scope,"").Items).Id);
    }

    [Fact] public void Preview_replacement_approval_cannot_overwrite_a_target_corrected_after_preview()
    {
        Configure(); Apply(Proposal());
        Configure(UnderstandingMode.Off);
        var target = Assert.Single(_service.List(_scope, false));
        var proposal = Proposal("preview-replacement", UnderstandingDomain.User, UnderstandingKind.Interpretation) with { TargetId = "interest", TargetRevision = target.Revision };
        Assert.Equal(UnderstandingOutcome.Failed, Assert.Single(Apply(proposal, "same-old-roots", manual: true).Items).Outcome);
        var newer = Root("new-material"); proposal = proposal with { Sources = [newer] };
        Assert.Equal(UnderstandingOutcome.ReviewedNotPersisted, Assert.Single(Apply(proposal, "new-preview", manual: true).Items).Outcome);
        _service.Review(_scope, "correct-target", "interest", target.Revision, UnderstandingAction.Correct, "new correction");
        Assert.Equal("UNDERSTANDING_STALE", Assert.Single(_service.Review(_scope, "save-preview", proposal.Id, 1, UnderstandingAction.Approve).Items).Code);
    }

    [Fact] public async Task Manual_preview_dispatches_once_with_fake_producer_and_failure_is_not_retried_or_refunded()
    {
        _service.Configure(_scope, 0, new(ReadSources: true, ExternalProcessing: true));
        var calls = 0;
        Task<IReadOnlyList<UnderstandingProposal>> Produce(CancellationToken token)
        { calls++; return Task.FromResult<IReadOnlyList<UnderstandingProposal>>([Proposal()]); }
        Assert.Equal(UnderstandingOutcome.ReviewedNotPersisted, Assert.Single((await _service.PreviewAsync(_scope, "preview", [Root()], Produce)).Items).Outcome);
        await _service.PreviewAsync(_scope, "preview", [Root()], Produce);
        Assert.Equal(1, calls);
        Task<IReadOnlyList<UnderstandingProposal>> Fail(CancellationToken token) { calls++; throw new IOException("synthetic private exception"); }
        Assert.Equal("UNDERSTANDING_PREVIEW_FAILED", Assert.Single((await _service.PreviewAsync(_scope, "failure", [Root()], Fail)).Items).Code);
        await _service.PreviewAsync(_scope, "failure", [Root()], Fail);
        Assert.Equal(2, calls);
        Assert.Equal(2, _service.AttemptStates(_scope).Sum(a => a.ReservedCalls));
        using var db = _database.Open(); using var command = db.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM understanding_entries"; Assert.Equal(0L, command.ExecuteScalar());
        command.CommandText = "SELECT COUNT(*) FROM understanding_revisions"; Assert.Equal(0L, command.ExecuteScalar());
    }
    [Fact] public void Coverage_keeps_mixed_per_item_outcomes_and_blocks_automatic_redispatch_of_same_roots()
    {
        _service.Configure(_scope, 0, new(UnderstandingMode.Bounded, ReadSources: true, ExternalProcessing: true, SaveCandidates: true, UseInDialogue: true));
        var root = Root() with { SourceRevision = 1 };
        var proposals = new[] { Proposal(root: root), Proposal("pending", UnderstandingDomain.User, UnderstandingKind.Interpretation, root) };
        _service.Apply(_service.Begin(_scope, "mixed", [root], coverage: [new(root, 0, 4, 4)]), proposals);
        Assert.Equal(new[] { UnderstandingOutcome.Applied, UnderstandingOutcome.ProposedPending }, _service.Coverage(_scope, [root]).Select(i => i.Outcome));
        Assert.False(_service.TryReserveAttempt(_scope, "must-not-dispatch", [root], manual: false));
        var newRoot = Root("new");
        var noChange = _service.Apply(_service.Begin(_scope, "none", [newRoot]), []);
        Assert.Equal(UnderstandingOutcome.NoChange, Assert.Single(noChange.Items).Outcome);
        Assert.Empty(_service.Coverage(_scope, [Root("unprocessed")]));
    }
    [Fact] public void Approved_read_survives_automatic_off_but_full_disable_and_revoked_read_grant_are_distinct_denials()
    {
        Configure(); Apply(Proposal("pending", UnderstandingDomain.User, UnderstandingKind.Interpretation));
        _service.Review(_scope, "approve", "pending", 1, UnderstandingAction.Approve);
        Configure(UnderstandingMode.Off); Assert.Single(_service.Query(_scope, "").Items);
        var state = _service.State(_scope);
        _service.Configure(_scope, state.Revision, state.Policy with { ReadSources = false });
        Assert.Equal("UNDERSTANDING_NOT_AUTHORIZED", _service.Query(_scope, "").Code);
        state = _service.State(_scope); _service.Configure(_scope, state.Revision, state.Policy with { Enabled = false });
        Assert.Equal("UNDERSTANDING_DISABLED", _service.Query(_scope, "").Code);
    }
    [Fact] public void Revocation_is_checked_before_any_source_body_read_on_commit_or_approval()
    {
        Configure(); Apply(Proposal("pending", UnderstandingDomain.User, UnderstandingKind.Interpretation));
        var ticket = _service.Begin(_scope, "old-ticket", [Root()]);
        var state = _service.State(_scope); _service.Configure(_scope, state.Revision, state.Policy with { ReadSources = false });
        var reads = 0; _sources.OnCheck = () => reads++;
        Assert.Equal("UNDERSTANDING_NOT_AUTHORIZED", Assert.Single(_service.Apply(ticket, [Proposal("new")]).Items).Code);
        var pending = Assert.Single(_service.List(_scope, true));
        Assert.Equal("UNDERSTANDING_NOT_AUTHORIZED", Assert.Single(_service.Review(_scope, "forged-approval", pending.Id, pending.Revision, UnderstandingAction.Approve).Items).Code);
        Assert.Equal("UNDERSTANDING_NOT_AUTHORIZED", _service.Query(_scope, "").Code);
        Assert.Equal(0, reads);
    }
    [Fact] public void Source_validation_does_not_upgrade_unknown_or_partial_read_coverage()
    {
        Configure(); var p = Proposal();
        var receipt = _service.Apply(_service.Begin(_scope, "unknown-coverage", p.Sources), [p]);
        Assert.Equal(UnderstandingOutcome.ProposedPending, Assert.Single(receipt.Items).Outcome);
        Assert.Null(Assert.Single(_service.List(_scope, true)).Coverage);
        var partial = new UnderstandingSourceCoverage(Root("new-root"), 0, 2, 10);
        var fresh = Proposal("partial", root: partial.Source);
        _service.Apply(_service.Begin(_scope, "partial-coverage", fresh.Sources, coverage: [partial]), [fresh]);
        var entry = _service.List(_scope, true).Single(e => e.Id == "partial");
        Assert.False(Assert.Single(entry.Coverage!).Complete); Assert.Equal(2, entry.Coverage![0].IncludedScalars);
    }
    [Fact] public void Diagnostic_sink_sees_only_safe_metadata_and_cannot_change_commit()
    {
        var events = new List<UnderstandingDiagnostic>();
        var owner = new UnderstandingService(_database, _sources, _clock, diagnostics: d => { events.Add(d); throw new IOException("synthetic sink failure"); });
        owner.Configure(_scope, 0, new(UnderstandingMode.Proposals, ReadSources: true, SaveCandidates: true));
        var proposal = Proposal("private-candidate-id") with { Content = "private-body-sentinel", Conditions = "private-condition-sentinel" };
        var result = owner.Apply(owner.Begin(_scope, "private-operation-id", proposal.Sources), [proposal]);
        Assert.Equal(UnderstandingOutcome.ProposedPending, Assert.Single(result.Items).Outcome);
        Assert.NotEmpty(events); Assert.All(events, d => Assert.Matches("^[A-F0-9]{16}$", d.Correlation));
        var serialized = System.Text.Json.JsonSerializer.Serialize(events);
        Assert.DoesNotContain("private-", serialized); Assert.DoesNotContain("synthetic sink failure", serialized);
        Assert.Contains("source_validation_completed", serialized);
    }
    [Fact] public void Restoring_an_older_backup_cannot_reuse_a_current_state_or_entry_revision_number()
    {
        Configure(); Apply(Proposal());
        var backup = new RuntimeDatabase(Path.Combine(_directory, "older.db"), pooling: false);
        using (var source = _database.Open()) using (var target = backup.Open()) source.BackupDatabase(target);
        _service.Review(_scope, "correction-1", "interest", 1, UnderstandingAction.Correct, "current correction");
        _service.Review(_scope, "correction-2", "interest", 2, UnderstandingAction.Correct, "newest correction");
        var state = _service.State(_scope); var entry = Assert.Single(_service.List(_scope, false));
        _service.NormalizeRestoredCopy(backup.DatabasePath);
        var restored = new UnderstandingService(backup, _sources, _clock);
        Assert.True(restored.State(_scope).Revision > state.Revision);
        Assert.True(Assert.Single(restored.List(_scope, false)).Revision > entry.Revision);
        Assert.Empty(restored.Query(_scope, "").Items);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Restore_keeps_retraction_from_backup_or_current_state_and_refuses_reapproval(bool backupBeforeRetraction, bool originalBounded)
    {
        Configure();
        var original = originalBounded ? Proposal() : Proposal("original-entry", UnderstandingDomain.User, UnderstandingKind.Interpretation);
        Apply(original);
        if (!originalBounded) _service.Review(_scope, "approve-original", original.Id, 1, UnderstandingAction.Approve);
        var backup = new RuntimeDatabase(Path.Combine(_directory, "retraction-backup.db"), pooling: false);
        void CaptureBackup()
        {
            using var source = _database.Open(); using var target = backup.Open(); source.BackupDatabase(target);
        }
        if (backupBeforeRetraction) CaptureBackup();
        var replacement = Proposal("replacement", UnderstandingDomain.User, UnderstandingKind.Interpretation, Root("new-material")) with
        { TargetId = original.Id, TargetRevision = originalBounded ? 1 : 2 };
        Assert.Equal(UnderstandingOutcome.ProposedPending, Assert.Single(Apply(replacement, "replacement-batch").Items).Outcome);
        Assert.Equal(UnderstandingOutcome.Applied, Assert.Single(_service.Review(_scope, "approve-replacement", replacement.Id, 1, UnderstandingAction.Approve).Items).Outcome);
        var retracted = _service.List(_scope, false).Single(e => e.Id == original.Id);
        Assert.Equal(UnderstandingLife.Retracted, retracted.Life);
        Assert.Equal("UNDERSTANDING_REJECTED", Assert.Single(_service.Review(_scope, "restore-before-backup", original.Id,
            retracted.Revision, UnderstandingAction.Restore).Items).Code);
        if (!backupBeforeRetraction) CaptureBackup();

        _service.NormalizeRestoredCopy(backup.DatabasePath);
        var restored = new UnderstandingService(backup, _sources, _clock);
        var restoredOriginal = restored.List(_scope, false).Single(e => e.Id == original.Id);
        Assert.Equal(UnderstandingLife.Retracted, restoredOriginal.Life);
        Assert.True(restoredOriginal.Revision > retracted.Revision);
        Assert.Equal(new UnderstandingPolicy(), restored.State(_scope).Policy);
        restored.Configure(_scope, restored.State(_scope).Revision, new(ReadSources: true, UseInDialogue: true));
        restoredOriginal = restored.List(_scope, false).Single(e => e.Id == original.Id);
        Assert.Equal(UnderstandingLife.Retracted, restoredOriginal.Life);
        Assert.Empty(restored.Query(_scope, "", historical: true, entryId: original.Id).Items);
        foreach (var action in new[] { UnderstandingAction.Approve, UnderstandingAction.Restore, UnderstandingAction.Suspend, UnderstandingAction.Resolve })
            Assert.Equal("UNDERSTANDING_REJECTED", Assert.Single(restored.Review(_scope, "after-" + action, original.Id,
                restoredOriginal.Revision, action).Items).Code);
        Assert.Empty(restored.Query(_scope, "").Items);
        if (!backupBeforeRetraction)
        {
            var surviving = restored.List(_scope, false).Single(e => e.Id == replacement.Id);
            Assert.Equal(UnderstandingLife.Dormant, surviving.Life);
            Assert.Equal(UnderstandingOutcome.Applied, Assert.Single(restored.Review(_scope, "restore-survivor", surviving.Id,
                surviving.Revision, UnderstandingAction.Restore).Items).Outcome);
            Assert.Equal(replacement.Id, Assert.Single(restored.Query(_scope, "").Items).Id);
        }
    }

    [Theory]
    [InlineData(UnderstandingAction.Suspend, false)]
    [InlineData(UnderstandingAction.Resolve, false)]
    [InlineData(UnderstandingAction.Suspend, true)]
    [InlineData(UnderstandingAction.Resolve, true)]
    public void Retracted_review_cannot_reopen_through_suspend_or_resolve(UnderstandingAction action, bool originalBounded)
    {
        Configure();
        var original = originalBounded ? Proposal() : Proposal("original-entry", UnderstandingDomain.User, UnderstandingKind.Interpretation);
        Apply(original);
        if (!originalBounded) _service.Review(_scope, "approve-original", original.Id, 1, UnderstandingAction.Approve);
        var replacement = Proposal("replacement", UnderstandingDomain.User, UnderstandingKind.Interpretation, Root("new-material")) with
        { TargetId = original.Id, TargetRevision = originalBounded ? 1 : 2 };
        Apply(replacement, "replacement-batch");
        _service.Review(_scope, "approve-replacement", replacement.Id, 1, UnderstandingAction.Approve);
        var retracted = _service.List(_scope, false).Single(e => e.Id == original.Id);
        Assert.Equal(UnderstandingLife.Retracted, retracted.Life);
        var state = _service.State(_scope);
        var intermediate = _service.Review(_scope, "intermediate", original.Id, retracted.Revision, action);
        var afterIntermediate = _service.List(_scope, false).Single(e => e.Id == original.Id);
        var restore = _service.Review(_scope, "restore-after-intermediate", original.Id, afterIntermediate.Revision, UnderstandingAction.Restore);
        Assert.Equal("UNDERSTANDING_REJECTED", Assert.Single(intermediate.Items).Code);
        Assert.Equal("UNDERSTANDING_REJECTED", Assert.Single(restore.Items).Code);
        Assert.Equal(UnderstandingLife.Retracted, afterIntermediate.Life);
        Assert.Equal(retracted.Revision, afterIntermediate.Revision);
        Assert.Equal(retracted.Revision, _service.List(_scope, false).Single(e => e.Id == original.Id).Revision);
        Assert.Equal(state, _service.State(_scope));
        Assert.Empty(_service.Query(_scope, "", historical: true, entryId: original.Id).Items);
        Assert.Equal(replacement.Id, Assert.Single(_service.Query(_scope, "").Items).Id);
    }

    [Theory]
    [InlineData(UnderstandingAction.Suspend, UnderstandingLife.Dormant)]
    [InlineData(UnderstandingAction.Resolve, UnderstandingLife.Resolved)]
    public void Active_review_still_allows_suspend_resolve_and_exact_restore(UnderstandingAction action, UnderstandingLife life)
    {
        Configure(); Apply(Proposal());
        Assert.Equal(UnderstandingOutcome.Applied, Assert.Single(_service.Review(_scope, "active-review", "interest", 1, action).Items).Outcome);
        Assert.Equal(life, Assert.Single(_service.List(_scope, false)).Life);
        Assert.Empty(_service.Query(_scope, "").Items);
        Assert.Equal(UnderstandingOutcome.Applied, Assert.Single(_service.Review(_scope, "restore-active", "interest", 2, UnderstandingAction.Restore).Items).Outcome);
        Assert.Single(_service.Query(_scope, "").Items);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Restore_keeps_resolved_lifecycle_out_of_default_and_historical_projection(bool backupBeforeResolution)
    {
        Configure(); Apply(Proposal());
        var backup = new RuntimeDatabase(Path.Combine(_directory, "resolved-backup.db"), pooling: false);
        void CaptureBackup()
        {
            using var source = _database.Open(); using var target = backup.Open(); source.BackupDatabase(target);
        }
        if (backupBeforeResolution) CaptureBackup();
        _service.Review(_scope, "resolve", "interest", 1, UnderstandingAction.Resolve);
        if (!backupBeforeResolution) CaptureBackup();
        _service.NormalizeRestoredCopy(backup.DatabasePath);
        var restored = new UnderstandingService(backup, _sources, _clock);
        Assert.Equal(UnderstandingLife.Resolved, Assert.Single(restored.List(_scope, false)).Life);
        restored.Configure(_scope, restored.State(_scope).Revision, new(ReadSources: true, UseInDialogue: true));
        Assert.Empty(restored.Query(_scope, "").Items);
        Assert.Empty(restored.Query(_scope, "", historical: true, entryId: "interest").Items);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(41L)]
    [InlineData(42L)]
    public void Automatic_attempt_refuses_unattested_or_pre_enable_roots_without_reserving(long sourceRevision)
    {
        _service.Configure(_scope, 0, new(UnderstandingMode.Bounded, ReadSources: true, ExternalProcessing: true, SaveCandidates: true), 42);
        var root = Root() with { SourceRevision = sourceRevision };
        Assert.False(_service.TryReserveAttempt(_scope, "old-range", [root], manual: false));
        Assert.Empty(_service.AttemptStates(_scope));
        // The same explicit manual range remains subject to the existing read/model grants.
        Assert.True(_service.TryReserveAttempt(_scope, "old-range", [root], manual: true));
        Assert.Equal(("Reserved", 1), Assert.Single(_service.AttemptStates(_scope)));
    }

    [Theory]
    [InlineData(UnderstandingMode.Proposals)]
    [InlineData(UnderstandingMode.Bounded)]
    public void Automatic_attempt_without_save_grant_is_refused_but_explicit_manual_range_remains_allowed(UnderstandingMode mode)
    {
        _service.Configure(_scope, 0, new(mode, ReadSources: true, ExternalProcessing: true), 42);
        var root = Root() with { SourceRevision = 43 };
        Assert.False(_service.TryReserveAttempt(_scope, "no-save-grant", [root], manual: false));
        Assert.Empty(_service.AttemptStates(_scope));
        Assert.True(_service.TryReserveAttempt(_scope, "no-save-grant", [root], manual: true));
        Assert.Equal(("Reserved", 1), Assert.Single(_service.AttemptStates(_scope)));
    }

    [Theory]
    [InlineData(UnderstandingMode.Proposals, false)]
    [InlineData(UnderstandingMode.Bounded, false)]
    [InlineData(UnderstandingMode.Proposals, true)]
    [InlineData(UnderstandingMode.Bounded, true)]
    public void Staged_read_grant_captures_current_boundary_and_keeps_new_and_manual_ranges(UnderstandingMode mode, bool regrant)
    {
        var policy = new UnderstandingPolicy(mode, ReadSources: regrant, ExternalProcessing: true, SaveCandidates: true);
        var state = _service.Configure(_scope, 0, policy, regrant ? 10 : 0);
        if (regrant) state = _service.Configure(_scope, state.Revision, policy with { ReadSources = false });
        state = _service.Configure(_scope, state.Revision, policy with { ReadSources = true }, 42);
        foreach (var revision in new[] { 0L, 41L, 42L })
            Assert.False(_service.TryReserveAttempt(_scope, "old-" + revision, [Root("old-" + revision) with { SourceRevision = revision }], manual: false));
        Assert.Empty(_service.AttemptStates(_scope));
        Assert.Equal(42, state.AutoSince);
        // Other grants must not shift an existing read boundary or authorize an old-only range.
        state = _service.Configure(_scope, state.Revision, state.Policy with { UseInDialogue = true }, 100);
        Assert.Equal(42, state.AutoSince);
        var newRoot = Root("new-root") with { SourceRevision = 43 };
        var oldRoot = Root("old-background") with { SourceRevision = 41 };
        Assert.True(_service.TryReserveAttempt(_scope, "new-range", [oldRoot, newRoot], manual: false));
        Assert.True(_service.TryReserveAttempt(_scope, "manual-old-range", [oldRoot], manual: true));
        Assert.Equal(2, _service.AttemptStates(_scope).Count);
        Assert.All(_service.AttemptStates(_scope), attempt => Assert.Equal(("Reserved", 1), attempt));
    }

    [Fact] public void Read_grant_on_empty_timeline_keeps_zero_boundary_and_accepts_first_new_source()
    {
        var state = _service.Configure(_scope, 0, new(UnderstandingMode.Bounded, ExternalProcessing: true, SaveCandidates: true));
        state = _service.Configure(_scope, state.Revision, state.Policy with { ReadSources = true });
        Assert.Equal(0, state.AutoSince);
        Assert.True(_service.TryReserveAttempt(_scope, "first-source", [Root() with { SourceRevision = 1 }], manual: false));
        Assert.Single(_service.AttemptStates(_scope));
    }

    [Fact] public void Automatic_attempt_allows_new_range_with_authorized_old_background_and_still_deduplicates()
    {
        _service.Configure(_scope, 0, new(UnderstandingMode.Bounded, ReadSources: true, ExternalProcessing: true, SaveCandidates: true), 42);
        var oldRoot = Root("background") with { SourceRevision = 42 };
        var newRoot = Root("new-root") with { SourceRevision = 43 };
        Assert.True(_service.TryReserveAttempt(_scope, "new-range", [oldRoot, newRoot], manual: false));
        Assert.False(_service.TryReserveAttempt(_scope, "duplicate-range", [newRoot, oldRoot], manual: false));
        Assert.Equal(("Reserved", 1), Assert.Single(_service.AttemptStates(_scope)));
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    private sealed class Sources : IConversationSourceReader
    {
        public bool Current = true;
        public Action? OnCheck;
        public ConversationSourceSnapshot Read(ToolScope scope, string messageId, ConversationSourceKind kind) => throw new NotSupportedException();
        public bool IsCurrent(ConversationSourceSnapshot source) { OnCheck?.Invoke(); return Current; }
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 18, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class AuditedFixtures : IUnderstandingRoutingPolicy
    {
        public bool IsLowImpact(UnderstandingProposal proposal, IReadOnlyList<ConversationSourceSnapshot> roots) => proposal.Id == "interest";
    }
}
