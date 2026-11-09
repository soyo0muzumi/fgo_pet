using FgoPet.Core.Dialogue;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Understanding;
using FgoPet.Understanding.Contracts;
using Xunit;

namespace FgoPet.Plugin.Understanding.Tests;

public sealed class TopicMaterialStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "fgopet-material-" + Guid.NewGuid().ToString("N"));
    private readonly Clock _clock = new();
    private readonly ConversationScope _scope = new("mash", "project-a");
    private readonly RuntimeDatabase _database;

    public TopicMaterialStoreTests() => _database = new(Path.Combine(_directory, "runtime.db"), pooling: false);
    private TopicMaterial Material(string id = "material", string topic = "interest", string? url = null) =>
        new(id, topic, 1, TopicMaterialScope.Interest, "Selective forgetting can suppress weak associations.",
            "Connects to the active memory experiment.", "Synthetic source", url ?? "https://example.org/" + id,
            _clock.Now, _clock.Now.AddDays(1));

    [Fact]
    public void Material_survives_restart_with_provenance_and_exact_scope_isolation()
    {
        var store = new TopicMaterialStore(_database, _clock);
        var material = Material();
        store.Add(_scope, material);
        var reopened = new TopicMaterialStore(_database, _clock);
        Assert.Equal(material, Assert.Single(reopened.GetActive(_scope)));
        Assert.Empty(reopened.GetActive(new("mash", "project-b")));
        Assert.Empty(reopened.GetActive(new("other", "project-a")));
    }

    [Fact]
    public void Evidence_reads_provenance_without_creating_or_pruning_storage()
    {
        var store = new TopicMaterialStore(_database, _clock);
        ITopicMaterialEvidence evidence = store;
        Assert.Empty(evidence.ReadEvidence(_scope));
        Assert.False(File.Exists(_database.DatabasePath));

        var material = Material();
        store.Add(_scope, material);
        var active = Assert.Single(evidence.ReadEvidence(_scope));
        Assert.Equal("external_material", active.SourceKind);
        Assert.Equal(material.SourceUrl, active.SourceUrl);
        Assert.Equal(material.ExpiresAt, active.ExpiresAt);
        Assert.Empty(evidence.ReadEvidence(new("mash", "project-b")));

        _clock.Now = material.ExpiresAt;
        Assert.Empty(evidence.ReadEvidence(_scope));
        using var db = _database.Open();
        using var count = db.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM topic_materials";
        Assert.Equal(1L, count.ExecuteScalar());
        store.PruneExpired();
        Assert.Equal(0L, count.ExecuteScalar());
    }

    [Fact]
    public void Expiration_and_capacity_prune_oldest_material_without_touching_other_scopes()
    {
        var store = new TopicMaterialStore(_database, _clock, capacity: 2);
        store.Add(_scope, Material("old"));
        store.Add(new("other", null), Material("other"));
        _clock.Now = _clock.Now.AddMinutes(1);
        store.Add(_scope, Material("middle") with { Summary = "A second independent fact." });
        _clock.Now = _clock.Now.AddMinutes(1);
        store.Add(_scope, Material("new") with { Summary = "A third independent fact." });
        Assert.Equal(new[] { "new", "middle" }, store.GetActive(_scope).Select(m => m.Id));
        Assert.Single(store.GetActive(new("other", null)));
        _clock.Now = _clock.Now.AddDays(2);
        Assert.Empty(store.GetActive(_scope));
        using var db = _database.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM topic_materials";
        Assert.Equal(0L, cmd.ExecuteScalar());
    }

    [Fact]
    public void Duplicate_source_or_summary_does_not_accumulate_or_reset_use_signal()
    {
        var store = new TopicMaterialStore(_database, _clock);
        store.Add(_scope, Material());
        store.MarkUsed(_scope, "material", _clock.Now);
        store.Add(_scope, Material("same-url", url: "https://example.org/material") with { Summary = "Changed snippet." });
        store.Add(_scope, Material("same-summary"));
        var retained = Assert.Single(store.GetActive(_scope));
        Assert.Equal("material", retained.Id);
        Assert.Equal(_clock.Now, retained.LastUsedAt);
    }

    [Fact]
    public void Mark_used_is_scoped_and_does_not_permanently_remove_material()
    {
        var store = new TopicMaterialStore(_database, _clock);
        store.Add(_scope, Material());
        store.MarkUsed(new("mash", "project-b"), "material", _clock.Now);
        Assert.Null(Assert.Single(store.GetActive(_scope)).LastUsedAt);
        store.MarkUsed(_scope, "material", _clock.Now);
        _clock.Now = _clock.Now.AddHours(3);
        Assert.NotNull(Assert.Single(store.GetActive(_scope)).LastUsedAt);
    }

    [Fact]
    public void Old_topic_revision_does_not_block_material_for_the_current_revision()
    {
        var store = new TopicMaterialStore(_database, _clock);
        store.Add(_scope, Material("old"));
        store.Add(_scope, Material("new", url: "https://example.org/old") with { TopicRevision = 2 });
        Assert.Equal(new[] { 1, 2 }, store.GetActive(_scope).Select(material => material.TopicRevision).Order());
    }

    [Fact]
    public void Research_reservation_survives_restart_failed_work_and_clock_rollback()
    {
        var store = new TopicMaterialStore(_database, _clock);
        Assert.True(store.TryReserveResearch(_scope, "interest", _clock.Now, TimeSpan.FromHours(4)));
        var restarted = new TopicMaterialStore(_database, _clock);
        Assert.False(restarted.TryReserveResearch(_scope, "interest", _clock.Now, TimeSpan.FromHours(4)));
        _clock.Now = _clock.Now.AddHours(-1);
        Assert.False(restarted.TryReserveResearch(_scope, "interest", _clock.Now, TimeSpan.FromHours(4)));
        _clock.Now = _clock.Now.AddHours(5);
        Assert.True(restarted.TryReserveResearch(_scope, "interest", _clock.Now, TimeSpan.FromHours(4)));
        Assert.True(restarted.TryReserveResearch(new("mash", "project-b"), "interest", _clock.Now, TimeSpan.FromHours(4)));
    }

    [Fact]
    public void Active_research_reservations_are_bounded_without_evicting_cooldowns()
    {
        var store = new TopicMaterialStore(_database, _clock, capacity: 2);
        Assert.True(store.TryReserveResearch(_scope, "a", _clock.Now, TimeSpan.FromHours(4)));
        _clock.Now = _clock.Now.AddMinutes(5);
        Assert.True(store.TryReserveResearch(_scope, "b", _clock.Now, TimeSpan.FromHours(4)));
        _clock.Now = _clock.Now.AddMinutes(5);
        Assert.False(store.TryReserveResearch(_scope, "c", _clock.Now, TimeSpan.FromHours(4)));
        Assert.False(store.TryReserveResearch(_scope, "a", _clock.Now, TimeSpan.FromHours(4)));
        _clock.Now = _clock.Now.AddHours(4);
        Assert.True(store.TryReserveResearch(_scope, "c", _clock.Now, TimeSpan.FromHours(4)));
    }

    [Fact]
    public void Delete_scope_removes_material_and_cooldown_only_for_that_scope()
    {
        var store = new TopicMaterialStore(_database, _clock);
        var other = new ConversationScope("mash", "project-b");
        store.Add(_scope, Material()); store.Add(other, Material());
        store.TryReserveResearch(_scope, "interest", _clock.Now, TimeSpan.FromHours(4));
        store.TryReserveResearch(other, "interest", _clock.Now, TimeSpan.FromHours(4));
        store.DeleteScope(_scope);
        Assert.Empty(store.GetActive(_scope)); Assert.Single(store.GetActive(other));
        Assert.False(store.TryReserveResearch(_scope, "interest", _clock.Now, TimeSpan.FromHours(4)));
        _clock.Now = _clock.Now.AddMinutes(5);
        Assert.True(store.TryReserveResearch(_scope, "interest", _clock.Now, TimeSpan.FromHours(4)));
        Assert.False(store.TryReserveResearch(other, "interest", _clock.Now, TimeSpan.FromHours(4)));
    }

    [Fact]
    public void Forget_conversation_conservatively_removes_role_materials_without_refunding_budget()
    {
        var store = new TopicMaterialStore(_database, _clock);
        var otherProject = new ConversationScope("mash", "project-b");
        var otherRole = new ConversationScope("other", "project-a");
        store.Add(_scope, Material()); store.Add(otherProject, Material()); store.Add(otherRole, Material());
        Assert.True(store.TryReserveResearch(_scope, "interest", _clock.Now, TimeSpan.FromHours(4), dailyLimit: 1));
        store.ForgetConversation("synthetic-conversation", "mash");
        Assert.Empty(store.GetActive(_scope)); Assert.Empty(store.GetActive(otherProject));
        Assert.Single(store.GetActive(otherRole));
        _clock.Now = _clock.Now.AddMinutes(5);
        Assert.False(store.TryReserveResearch(_scope, "new-topic", _clock.Now, TimeSpan.FromHours(4), dailyLimit: 1));
        store.ForgetAll();
        Assert.Empty(store.GetActive(otherRole));
        Assert.False(store.TryReserveResearch(_scope, "new-topic", _clock.Now, TimeSpan.FromHours(4), dailyLimit: 1));
        Assert.Equal(0, store.Export().GetArrayLength());
    }

    [Fact]
    public void Restore_discards_old_material_and_unions_later_research_charges_and_cooldowns()
    {
        var store = new TopicMaterialStore(_database, _clock);
        store.Add(_scope, Material());
        Assert.True(store.TryReserveResearch(_scope, "first", _clock.Now, TimeSpan.FromHours(4), dailyLimit: 2));
        var backup = new RuntimeDatabase(Path.Combine(_directory, "backup.db"), pooling: false);
        using (var current = _database.Open()) using (var target = backup.Open()) current.BackupDatabase(target);
        _clock.Now = _clock.Now.AddMinutes(5);
        Assert.True(store.TryReserveResearch(_scope, "second", _clock.Now, TimeSpan.FromHours(4), dailyLimit: 2));
        store.NormalizeRestoredCopy(backup.DatabasePath);
        var restored = new TopicMaterialStore(backup, _clock);
        Assert.Empty(restored.GetActive(_scope));
        Assert.Single(store.GetActive(_scope));
        _clock.Now = _clock.Now.AddMinutes(5);
        Assert.False(restored.TryReserveResearch(_scope, "third", _clock.Now, TimeSpan.FromHours(4), dailyLimit: 2));
        Assert.False(restored.TryReserveResearch(_scope, "second", _clock.Now, TimeSpan.FromHours(4)));
        Assert.Throws<ArgumentException>(() => store.NormalizeRestoredCopy(_database.DatabasePath));
    }

    [Fact]
    public void Research_budget_is_rolling_persistent_scoped_and_never_refunded()
    {
        for (var index = 0; index < 6; index++)
        {
            var store = new TopicMaterialStore(_database, _clock);
            Assert.True(store.TryReserveResearch(_scope, "topic-" + index, _clock.Now, TimeSpan.FromHours(4)));
            _clock.Now = _clock.Now.AddMinutes(5);
        }
        var restarted = new TopicMaterialStore(_database, _clock);
        Assert.False(restarted.TryReserveResearch(_scope, "seventh", _clock.Now, TimeSpan.FromHours(4)));
        Assert.True(restarted.TryReserveResearch(new("other", "project-a"), "seventh", _clock.Now, TimeSpan.FromHours(4)));
        _clock.Now = _clock.Now.AddHours(24);
        Assert.True(restarted.TryReserveResearch(_scope, "seventh", _clock.Now, TimeSpan.FromHours(4)));
    }

    [Fact]
    public void Research_interval_and_custom_lower_daily_limit_are_enforced_across_topics()
    {
        var store = new TopicMaterialStore(_database, _clock);
        Assert.True(store.TryReserveResearch(_scope, "a", _clock.Now, TimeSpan.FromHours(4), dailyLimit: 2));
        Assert.False(store.TryReserveResearch(_scope, "b", _clock.Now, TimeSpan.FromHours(4), dailyLimit: 2));
        _clock.Now = _clock.Now.AddMinutes(5);
        Assert.True(store.TryReserveResearch(_scope, "b", _clock.Now, TimeSpan.FromHours(4), dailyLimit: 2));
        _clock.Now = _clock.Now.AddMinutes(5);
        Assert.False(store.TryReserveResearch(_scope, "c", _clock.Now, TimeSpan.FromHours(4), dailyLimit: 2));
        Assert.Throws<ArgumentException>(() => store.TryReserveResearch(_scope, "a", _clock.Now, TimeSpan.FromMinutes(5)));
        Assert.Throws<ArgumentException>(() => store.TryReserveResearch(_scope, "a", _clock.Now, TimeSpan.FromHours(4), dailyLimit: 7));
    }

    [Fact]
    public void Readonly_eligibility_skips_recent_topic_without_charging_another_topic()
    {
        var store = new TopicMaterialStore(_database, _clock);
        var refresh = TimeSpan.FromHours(4);
        Assert.True(store.TryReserveResearch(_scope, "recent", _clock.Now, refresh));
        _clock.Now = _clock.Now.AddMinutes(5);
        Assert.False(store.CanResearch(_scope, "recent", _clock.Now, refresh));
        Assert.True(store.CanResearch(_scope, "new", _clock.Now, refresh));
        Assert.True(store.CanResearch(_scope, "new", _clock.Now, refresh));
        Assert.True(store.TryReserveResearch(_scope, "new", _clock.Now, refresh));
    }

    [Fact]
    public void Readonly_eligibility_obeys_global_interval_and_persistent_daily_budget()
    {
        var store = new TopicMaterialStore(_database, _clock);
        var refresh = TimeSpan.FromHours(4);
        Assert.True(store.CanResearch(_scope, "a", _clock.Now, refresh, dailyLimit: 2));
        Assert.True(store.TryReserveResearch(_scope, "a", _clock.Now, refresh, dailyLimit: 2));
        Assert.False(store.CanResearch(_scope, "b", _clock.Now, refresh, dailyLimit: 2));
        _clock.Now = _clock.Now.AddMinutes(5);
        Assert.True(store.CanResearch(_scope, "b", _clock.Now, refresh, dailyLimit: 2));
        Assert.True(store.TryReserveResearch(_scope, "b", _clock.Now, refresh, dailyLimit: 2));
        _clock.Now = _clock.Now.AddMinutes(5);
        var reopened = new TopicMaterialStore(_database, _clock);
        Assert.False(reopened.CanResearch(_scope, "c", _clock.Now, refresh, dailyLimit: 2));
        Assert.True(reopened.CanResearch(new("other", "project-a"), "c", _clock.Now, refresh, dailyLimit: 2));
    }

    [Fact]
    public async Task Concurrent_store_instances_cannot_spend_a_shared_dispatch_slot_twice()
    {
        var first = new TopicMaterialStore(_database, _clock);
        var second = new TopicMaterialStore(_database, _clock);
        var refresh = TimeSpan.FromHours(4);
        Assert.True(first.CanResearch(_scope, "first", _clock.Now, refresh));
        Assert.True(second.CanResearch(_scope, "second", _clock.Now, refresh));
        var attempts = await Task.WhenAll(
            Task.Run(() => first.TryReserveResearch(_scope, "first", _clock.Now, refresh)),
            Task.Run(() => second.TryReserveResearch(_scope, "second", _clock.Now, refresh)));
        Assert.Single(attempts.Where(accepted => accepted));
    }

    [Theory]
    [InlineData("file:///c:/secret")]
    [InlineData("https://user:password@example.org/page")]
    [InlineData("http://localhost/page")]
    [InlineData("http://127.0.0.1/page")]
    [InlineData("http://10.0.0.1/page")]
    [InlineData("http://[::1]/page")]
    [InlineData("http://[fe80::1]/page")]
    [InlineData("http://server.local/page")]
    public void Unsafe_provenance_urls_are_rejected(string url)
    {
        var store = new TopicMaterialStore(_database, _clock);
        Assert.Throws<ArgumentException>(() => store.Add(_scope, Material(url: url)));
        Assert.Empty(store.GetActive(_scope));
    }

    [Fact]
    public void Invalid_content_revision_enum_or_lifetime_never_persists()
    {
        var store = new TopicMaterialStore(_database, _clock);
        TopicMaterial[] invalid = [Material() with { TopicRevision = 0 }, Material() with { Summary = new('x', 1201) },
            Material() with { Scope = (TopicMaterialScope)99 }, Material() with { ExpiresAt = _clock.Now.AddDays(8) },
            Material() with { ExpiresAt = _clock.Now }, Material() with { FetchedAt = _clock.Now.AddHours(1) },
            Material() with { RelevanceReason = " " }, Material() with { SourceTitle = "" }];
        foreach (var material in invalid) Assert.Throws<ArgumentException>(() => store.Add(_scope, material));
        Assert.Empty(store.GetActive(_scope));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 31, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
