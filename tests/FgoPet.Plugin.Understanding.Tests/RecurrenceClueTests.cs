using System.Text;
using FgoPet.Core.Dialogue;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Understanding;
using FgoPet.Understanding.Contracts;
using Xunit;

namespace FgoPet.Plugin.Understanding.Tests;

public sealed class RecurrenceClueTests
{
    private readonly RuntimeDatabase _db = new(Path.Combine(Path.GetTempPath(), "fgo-clue-" + Guid.NewGuid().ToString("N"), "runtime.db"), pooling: false);
    private readonly ConversationScope _scope = new("mash", "p");
    private readonly ReviewWorkQueueTests.Timeline _sources = new();
    private readonly Clock _clock = new();
    private readonly UnderstandingService _owner;
    public RecurrenceClueTests()
    {
        new RuntimeDatabaseMigrator(_db).Migrate();
        _owner = new(_db, _sources, _clock);
        Enable();
        _sources.Add(2);
    }
    private void Enable() => _owner.Configure(_scope, _owner.State(_scope).Revision,
        new(UnderstandingMode.Bounded, ReadSources: true, SaveCandidates: true, ExternalProcessing: true, UseInDialogue: true));
    private UnderstandingReceipt Save(string operation = "weak-event", string quote = "xxx")
    {
        var material = _sources.Heads.Select(h => new ReviewOriginal(h.Source, new string('x', h.TotalScalars),
            new(h.Source, 0, h.TotalScalars, h.TotalScalars))).ToArray();
        var ticket = _owner.Begin(_scope, operation, material.Select(o => o.Source).ToArray(), coverage: material.Select(o => o.Coverage).ToArray());
        return _owner.Apply(ticket, [], originals: material,
            clues: [new("clarifying disagreements", "Assistant asked for reasons before choosing under no time pressure.",
                [new(material[1].Source, quote)])], clueHeads: _sources.Heads);
    }
    [Fact] public void No_op_can_retain_a_clue_across_restart_without_creating_any_Self_or_projection()
    {
        Save();
        var restarted = new UnderstandingService(_db, _sources, _clock);
        var clue = Assert.Single(restarted.RecurrenceClues(_scope));
        Assert.Equal(TimeSpan.FromDays(14), clue.ExpiresAt - clue.ObservedAt);
        Assert.False(clue.Projectable);
        Assert.Equal(2, clue.SourceRefs.Count);
        Assert.Empty(restarted.List(_scope, true)); Assert.Empty(restarted.List(_scope, false));
        Assert.Empty(restarted.Query(_scope, "clarifying").Items);
        var exported = restarted.Export().GetRawText();
        Assert.DoesNotContain("Assistant asked", exported);
    }
    [Fact] public void Expiry_scope_and_source_change_prevent_retrieval()
    {
        Save(); Assert.Empty(_owner.RecurrenceClues(new("mash", "other")));
        _sources.Current = false; Assert.Empty(_owner.RecurrenceClues(_scope));
        _sources.Current = true; _clock.Now = _clock.Now.AddDays(14);
        Assert.Empty(_owner.RecurrenceClues(_scope));
    }
    [Fact] public void Policy_revocation_and_reenable_cannot_resurrect_old_clues()
    {
        Save(); _owner.Configure(_scope, _owner.State(_scope).Revision, new());
        Assert.Empty(_owner.RecurrenceClues(_scope)); Enable(); Assert.Empty(_owner.RecurrenceClues(_scope));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void Forget_removes_clue_payload_and_refs(bool all)
    {
        Save(); if (all) _owner.ForgetAll(); else _owner.ForgetConversation("c", "mash");
        using var db = _db.Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM understanding_recurrence_clues";
        Assert.Equal(0L, cmd.ExecuteScalar());
    }
    [Fact] public void Unquoted_or_user_only_signal_is_rejected_atomically()
    {
        Assert.Throws<ArgumentException>(() => Save(quote: "not present"));
        Assert.Empty(_owner.RecurrenceClues(_scope)); Assert.Null(_owner.Receipt(_scope, "weak-event"));
    }
    [Fact] public void Receipt_replay_never_duplicates_a_clue_or_renews_its_TTL()
    {
        Save(); var old = Assert.Single(_owner.RecurrenceClues(_scope));
        _clock.Now = _clock.Now.AddDays(2); Save();
        var replay = Assert.Single(_owner.RecurrenceClues(_scope));
        Assert.Equal(old.Id, replay.Id); Assert.Equal(old.ExpiresAt, replay.ExpiresAt);
        Assert.Equal(old.SourceRefs, replay.SourceRefs);
    }
    [Fact] public void Restored_copy_discards_transient_hints_without_modifying_the_current_database()
    {
        Save(); var stagedPath = Path.Combine(Path.GetDirectoryName(_db.DatabasePath)!, "staged.db");
        var staging = new RuntimeDatabase(stagedPath, pooling: false);
        using (var source = _db.Open()) using (var destination = staging.Open()) source.BackupDatabase(destination);
        _owner.NormalizeRestoredCopy(stagedPath);
        Assert.Single(_owner.RecurrenceClues(_scope));
        using var db = staging.Open(); using var command = db.CreateCommand();
        command.CommandText = "SELECT count(*) FROM understanding_recurrence_clues";
        Assert.Equal(0L, command.ExecuteScalar());
    }
    [Fact] public void Clue_store_is_bounded_and_never_discards_newest_for_older_hints()
    {
        Save("oldest"); var oldest = Assert.Single(_owner.RecurrenceClues(_scope)).Id;
        for (var n = 0; n < 65; n++) { _clock.Now = _clock.Now.AddMinutes(1); Save("event-" + n); }
        var clues = _owner.RecurrenceClues(_scope);
        Assert.Equal(64, clues.Count); Assert.DoesNotContain(clues, c => c.Id == oldest);
        Assert.Empty(_owner.Query(_scope, "").Items);
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-10-27T00:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
