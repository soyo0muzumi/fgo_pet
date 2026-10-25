using System.IO;
using FgoPet.App.Companion;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Settings;
using FgoPet.Extensibility;
using FgoPet.Kernel.Companion;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FgoPet.App.Tests.Companion;

public sealed class CompanionAwarenessStoreTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 10, 0, 0, TimeSpan.Zero);
    private static readonly ToolScope Scope = new("conversation-1", "mash", "project-1");
    private static readonly ModelRouteKey Route = ModelRouteKey.From(new ModelConnectionSettings(
        "synthetic", "https://synthetic.invalid/v1", "test-model"));
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fgo-awareness-store-" + Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_root, "awareness.db");
    private CompanionAwarenessStore Open() => new(DatabasePath);

    [Fact]
    public void New_store_defaults_to_internal_context_and_persists_explicit_recipient_approval()
    {
        var store = Open();
        var key = CompanionAwarenessStore.ScopeKey(Scope);
        var initial = store.Read(key);
        Assert.Equal(new CompanionAwarenessOptions(), initial.Options);
        Assert.True(initial.Options.ActivityContextEnabled);
        Assert.False(initial.Options.ApplicationsEnabled);
        Assert.False(initial.Options.BackgroundEnabled);
        Assert.Null(initial.ApprovedRoute);
        Assert.Equal(0, store.Used(Now));

        var policy = new CompanionAwarenessPolicy(new(false, true, true, 3), "approved-recipient-fingerprint");
        store.Save(key, policy);
        Assert.Equal(policy, Open().Read(key));
        Assert.Null(Open().Read(CompanionAwarenessStore.ScopeKey(Scope with { RoleId = "other-role" })).ApprovedRoute);
    }

    [Fact]
    public void Quota_is_global_across_restart_roles_projects_and_forgetting_source_policies()
    {
        var store = Open();
        var firstKey = CompanionAwarenessStore.ScopeKey(Scope);
        var otherScope = new ToolScope("conversation-2", "other-role", "other-project");
        var otherKey = CompanionAwarenessStore.ScopeKey(otherScope);
        var policy = new CompanionAwarenessPolicy(new(true, true, true, 2), "approved-route");
        store.Save(firstKey, policy);
        store.Save(otherKey, policy);
        Assert.True(store.TryReserve(Reservation("first"), 2));

        var restarted = Open();
        Assert.True(restarted.TryReserve(Reservation("second", scope: otherScope), 2));
        restarted.ForgetPolicies(firstKey);
        Assert.Null(restarted.Read(firstKey).ApprovedRoute);
        Assert.Equal(policy, restarted.Read(otherKey));
        restarted.ForgetPolicies();
        Assert.Null(restarted.Read(otherKey).ApprovedRoute);

        var afterForget = Open();
        Assert.Equal(2, afterForget.Used(Now));
        Assert.False(afterForget.TryReserve(Reservation("third", scope: otherScope), 2));
        Assert.Equal(2, afterForget.Used(Now));
    }

    [Fact]
    public void A_dispatched_activity_id_cannot_be_charged_twice_even_with_another_scope_or_after_restart()
    {
        var store = Open();
        Assert.True(store.TryReserve(Reservation("same-activity"), 6));
        var replay = Reservation("same-activity", scope: Scope with { RoleId = "other-role" });
        Assert.False(Open().TryReserve(replay, 6));
        Assert.Equal(1, Open().Used(Now));
        Assert.True(Open().TryReserve(Reservation("different-activity"), 6));
        Assert.Equal(2, Open().Used(Now));
    }

    [Fact]
    public void Clock_rollback_cannot_reopen_the_rolling_quota_and_the_high_water_survives_restart()
    {
        var store = Open();
        Assert.True(store.TryReserve(Reservation("first"), 1));
        Assert.Equal(1, store.Used(Now.AddHours(12)));

        var restarted = Open();
        Assert.Equal(1, restarted.Used(Now.AddDays(-2)));
        Assert.False(restarted.TryReserve(Reservation("rollback", at: Now.AddDays(-2)), 1));
        Assert.Equal(1, restarted.Used(Now.AddHours(24).AddMilliseconds(-1)));
        Assert.Equal(0, restarted.Used(Now.AddHours(24)));
        Assert.True(restarted.TryReserve(Reservation("next-window", at: Now.AddDays(-2)), 1));

        var again = Open();
        Assert.Equal(1, again.Used(Now));
        Assert.False(again.TryReserve(Reservation("another-rollback", at: Now), 1));
        Assert.Equal(1, again.Used(Now.AddHours(48).AddMilliseconds(-1)));
        Assert.Equal(0, again.Used(Now.AddHours(48)));
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("corrupt")]
    public void Deleted_or_corrupt_initialized_storage_never_recreates_an_empty_budget(string failure)
    {
        var store = Open();
        Assert.True(store.TryReserve(Reservation("charged"), 1));
        if (failure == "delete") File.Delete(DatabasePath);
        else File.WriteAllText(DatabasePath, "synthetic damaged database");

        Assert.Equal(-1, store.Used(Now));
        Assert.False(store.Available);
        Assert.False(store.TryReserve(Reservation("must-not-dispatch"), 1));
        var restarted = Open();
        Assert.False(restarted.Available);
        Assert.Equal(-1, restarted.Used(Now));
        Assert.False(restarted.TryReserve(Reservation("must-not-dispatch-after-restart"), 1));
        Assert.Throws<InvalidOperationException>(() => restarted.Read(CompanionAwarenessStore.ScopeKey(Scope)));
        Assert.True(File.Exists(DatabasePath + ".initialized"));
        if (failure == "delete") Assert.False(File.Exists(DatabasePath));
        else Assert.Equal("synthetic damaged database", File.ReadAllText(DatabasePath));
    }

    [Fact]
    public void A_running_store_does_not_reinitialize_when_both_database_and_marker_disappear()
    {
        var store = Open();
        Assert.True(store.TryReserve(Reservation("charged"), 1));
        File.Delete(DatabasePath);
        File.Delete(DatabasePath + ".initialized");
        Assert.False(store.Available);
        Assert.False(store.TryReserve(Reservation("must-not-dispatch"), 1));
        Assert.False(File.Exists(DatabasePath));
    }

    [Fact]
    public void An_unknown_schema_version_blocks_reopening_instead_of_resetting_the_ledger()
    {
        Assert.True(Open().TryReserve(Reservation("charged"), 1));
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath, Pooling = false, Mode = SqliteOpenMode.ReadWrite
        }.ToString()))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "UPDATE ledger_state SET version=99 WHERE id=1";
            command.ExecuteNonQuery();
        }
        var restarted = Open();
        Assert.False(restarted.Available);
        Assert.Equal(-1, restarted.Used(Now));
        Assert.False(restarted.TryReserve(Reservation("blocked"), 1));
    }

    [Theory]
    [InlineData(0, 100, 100, "activity")]
    [InlineData(25, 100, 100, "activity")]
    [InlineData(6, 0, 100, "activity")]
    [InlineData(6, 4097, 100, "activity")]
    [InlineData(6, 100, 0, "activity")]
    [InlineData(6, 100, 385, "activity")]
    [InlineData(6, 100, 100, "")]
    [InlineData(6, 100, 100, " ")]
    public void Out_of_policy_reservations_never_consume_quota(int dailyLimit, int input, int output, string activity)
    {
        var store = Open();
        Assert.False(store.TryReserve(Reservation(activity) with { InputTokens = input, MaxOutputTokens = output }, dailyLimit));
        Assert.Equal(0, store.Used(Now));
    }

    [Fact]
    public async Task Independent_store_instances_cannot_both_take_the_last_request()
    {
        Assert.True(Open().Available);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Task.Run(async () => { await start.Task; return Open().TryReserve(Reservation("first"), 1); });
        var second = Task.Run(async () => { await start.Task; return Open().TryReserve(Reservation("second"), 1); });
        start.SetResult();
        var outcomes = await Task.WhenAll(first, second);
        Assert.Single(outcomes.Where(accepted => accepted));
        Assert.Equal(1, Open().Used(Now));
    }

    [Fact]
    public void Consent_is_role_and_project_scoped_while_conversation_changes_keep_the_same_policy()
    {
        var key = CompanionAwarenessStore.ScopeKey(Scope);
        Assert.Equal(key, CompanionAwarenessStore.ScopeKey(Scope with { ConversationId = "next-conversation" }));
        Assert.NotEqual(key, CompanionAwarenessStore.ScopeKey(Scope with { RoleId = "other-role" }));
        Assert.NotEqual(key, CompanionAwarenessStore.ScopeKey(Scope with { ProjectId = "other-project" }));
        Assert.NotEqual(CompanionAwarenessStore.ScopeKey(new("c", "ab", "c")),
            CompanionAwarenessStore.ScopeKey(new("c", "a", "bc")));
    }

    [Fact]
    public void Restore_fence_blocks_every_old_scope_and_reapproving_one_scope_does_not_restore_the_others()
    {
        var store = Open();
        var first = CompanionAwarenessStore.ScopeKey(Scope);
        var second = CompanionAwarenessStore.ScopeKey(Scope with { RoleId = "other-role" });
        var previous = new CompanionAwarenessPolicy(new(true, true, true, 2), "old-recipient");
        store.Save(first, previous);
        store.Save(second, previous);
        Assert.True(store.TryReserve(Reservation("already-spent"), 2));
        var healthyCopy = File.ReadAllBytes(DatabasePath);

        // Model a damaged ledger during restore, followed by recovery of its former policy rows.
        // The independent fence must outlive both the failed store and the recovered database.
        File.WriteAllText(DatabasePath, "synthetic damaged database");
        Assert.True(store.RevokeForRestore());
        var marker = DatabasePath + ".permissions-revoked";
        Assert.True(File.Exists(marker));
        File.WriteAllBytes(DatabasePath, healthyCopy);
        var recovered = Open();
        Assert.Null(recovered.Read(first).ApprovedRoute);
        Assert.Null(recovered.Read(second).ApprovedRoute);
        Assert.Equal(previous.Options, recovered.Read(second).Options);
        Assert.Equal(1, recovered.Used(Now));

        var newlyApproved = previous with { ApprovedRoute = "newly-approved-recipient" };
        recovered.Save(first, newlyApproved);
        Assert.False(File.Exists(marker));
        var restarted = Open();
        Assert.Equal(newlyApproved, restarted.Read(first));
        Assert.Null(restarted.Read(second).ApprovedRoute);
        Assert.Equal(previous.Options, restarted.Read(second).Options);
        Assert.Equal(1, restarted.Used(Now));
    }

    private static CompanionActivityReservation Reservation(string activity, DateTimeOffset? at = null, ToolScope? scope = null)
        => new(activity, scope ?? Scope, Route, "policy-version", at ?? Now, 4096, 384);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
