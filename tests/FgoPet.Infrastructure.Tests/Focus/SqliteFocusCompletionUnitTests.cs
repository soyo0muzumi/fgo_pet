using FgoPet.Core.Bond;
using FgoPet.Core.Events;
using FgoPet.Core.Focus;
using FgoPet.Infrastructure.Bond;
using FgoPet.Infrastructure.Events;
using FgoPet.Infrastructure.Focus;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Infrastructure.Timeline;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FgoPet.Infrastructure.Tests.Focus;

public sealed class SqliteFocusCompletionUnitTests : IDisposable
{
    private static readonly DateTimeOffset Started = DateTimeOffset.Parse("2026-08-27T09:00:00Z");

    private readonly RuntimeDatabase _database;
    private readonly SqliteFocusCompletionUnit _unit;
    private readonly SqliteFocusRepository _repository;
    private readonly SqliteBondRepository _bond;

    public SqliteFocusCompletionUnitTests()
    {
        _database = new RuntimeDatabase(Path.Combine(Path.GetTempPath(), $"fgo-completion-{Guid.NewGuid():N}.db"));
        new RuntimeDatabaseMigrator(_database).Migrate();
        _repository = new SqliteFocusRepository(_database);
        _bond = new SqliteBondRepository(_database);
        _unit = new SqliteFocusCompletionUnit(
            _database,
            new SqliteEventStore(_database),
            new SqliteTimelineRepository(_database),
            _bond,
            new DefaultBondProgressionPolicy());
    }

    private FocusSession FocusingSession => FocusSession.Start(
        "session-1", "servant-mash", FocusPreset.Create(25, 5, 4), Started);

    private FocusSession BreakSession => FocusingSession with
    {
        Status = FocusStatus.Breaking,
        Phase = FocusPhase.Break,
        RemainingSeconds = 300,
    };

    private RuntimeEvent CompletedEvent => new(
        "event-focus-1", "session-1", RuntimeEventType.FocusCompleted,
        Started.AddMinutes(25), 1, FocusPhase.Focus, "servant-mash",
        ElapsedSeconds: 1_500, EffectiveSeconds: 1_500, Priority: 2);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var file = _database.DatabasePath + suffix;
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }

    [Fact]
    public void CompleteFocus_commits_event_timeline_ledger_and_bond_once()
    {
        _repository.SaveSnapshot(BreakSession);
        _unit.CompleteFocus(BreakSession, CompletedEvent);
        _unit.CompleteFocus(BreakSession, CompletedEvent);

        Assert.Equal(1, Count("runtime_events"));
        Assert.Equal(1, Count("timeline_entries"));
        Assert.Equal(1, Count("bond_ledger"));
        Assert.Equal(1_500, Scalar(
            "SELECT lifetime_focus_seconds FROM servant_bonds WHERE servant_id='servant-mash'"));
        Assert.Equal(1, Count("focus_sessions WHERE session_id='session-1' AND status='breaking' AND is_current=1"));
    }

    [Fact]
    public void CompleteFocus_rolls_back_every_write_when_a_write_fails()
    {
        _repository.SaveSnapshot(BreakSession);
        // Breaking the timeline table makes the write chain fail after the event insert;
        // every write must roll back, including the event and the snapshot upsert.
        CorruptForTest();

        Assert.Throws<SqliteException>(() => _unit.CompleteFocus(BreakSession, CompletedEvent));

        // Nothing from the completion transaction survives; the pre-existing snapshot does.
        Assert.Equal(0, Count("runtime_events"));
        Assert.Equal(0, Count("timeline_entries_hidden"));
        Assert.Equal(0, Count("bond_ledger"));
        Assert.Equal(0, Count("servant_bonds"));
        Assert.Equal(1, Count("focus_sessions WHERE status='breaking' AND is_current=1"));
    }

    [Fact]
    public void Snapshot_round_trips_and_LoadCurrent_returns_the_only_current_row()
    {
        var progressed = FocusingSession with { RemainingSeconds = 1_200, PhaseElapsedSeconds = 300 };

        _repository.SaveSnapshot(progressed);
        var loaded = _repository.LoadCurrent();

        Assert.NotNull(loaded);
        Assert.Equal("session-1", loaded!.SessionId);
        Assert.Equal(1_200, loaded.RemainingSeconds);
        Assert.Equal("servant-mash", loaded.ServantId);
        Assert.Equal(Started, loaded.StartedAtUtc);
        Assert.Equal(1, Count("focus_sessions WHERE is_current=1"));
    }

    [Fact]
    public void LoadCurrent_returns_null_when_no_session_is_current() =>
        Assert.Null(_repository.LoadCurrent());

    [Fact]
    public void CompleteFocus_emits_a_bond_level_up_event_and_entry_when_the_level_rises()
    {
        // 9600 seconds (= 2h40m) sits at level 2; one more 35-minute effective stage
        // crosses the level-3 threshold at 10800.
        _bond.Upsert(new("servant-mash", 9_600, 2, "bond-v1", Started));
        _repository.SaveSnapshot(BreakSession);
        _unit.CompleteFocus(BreakSession, CompletedEvent with { EffectiveSeconds = 2_100 });

        Assert.Equal(11_700, Scalar(
            "SELECT lifetime_focus_seconds FROM servant_bonds WHERE servant_id='servant-mash'"));
        Assert.Equal(3, Scalar("SELECT achieved_level FROM servant_bonds WHERE servant_id='servant-mash'"));
        Assert.Equal(1, Count($"runtime_events WHERE type='{RuntimeEventType.BondLevelUp}'"));
        Assert.Equal(2, Count("timeline_entries"));
        Assert.Equal(1, Count("bond_ledger"));
    }

    [Fact]
    public void GetBond_returns_the_stored_total_and_level()
    {
        _bond.Upsert(new("servant-mash", 7_200, 2, "bond-v1", Started));
        var bond = _bond.GetBond("servant-mash");

        Assert.Equal(7_200, bond!.LifetimeFocusSeconds);
        Assert.Equal(2, bond.AchievedLevel);
        Assert.Null(_bond.GetBond("servant-nobody"));
    }

    /// <summary>Renames a table the completion unit must write, so its write chain fails mid-transaction.</summary>
    private void CorruptForTest()
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "ALTER TABLE timeline_entries RENAME TO timeline_entries_hidden";
        command.ExecuteNonQuery();
    }

    private long Count(string predicate)
    {
        var space = predicate.IndexOf(' ');
        var tableName = space < 0 ? predicate : predicate[..space];
        var rest = space < 0 ? string.Empty : predicate[(space + 1)..];
        var where = rest.StartsWith("WHERE ", StringComparison.Ordinal) ? rest : string.IsNullOrEmpty(rest) ? string.Empty : $"WHERE {rest}";
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {tableName} {where}";
        return (long)command.ExecuteScalar()!;
    }

    private long Scalar(string sql)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }
}
