using System.IO;
using FgoPet.App.Focus;
using FgoPet.App.Runtime;
using FgoPet.Core.Bond;
using FgoPet.Core.Events;
using FgoPet.Core.Focus;
using FgoPet.Infrastructure.Bond;
using FgoPet.Infrastructure.Events;
using FgoPet.Infrastructure.Focus;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Infrastructure.Timeline;
using Xunit;

namespace FgoPet.App.Tests.Focus;

public sealed class FocusSessionServiceTests : IDisposable
{
    private static readonly DateTimeOffset Epoch = DateTimeOffset.Parse("2026-08-27T09:00:00Z");

    private readonly MutableTimeProvider _time;
    private readonly SqliteFocusRepository _repository;
    private readonly SqliteFocusCompletionUnit _completion;
    private readonly FocusSessionService _service;
    private readonly RuntimeDatabase _database;

    public FocusSessionServiceTests()
    {
        _time = new MutableTimeProvider(Epoch);
        _database = new RuntimeDatabase(Path.Combine(Path.GetTempPath(), $"fgo-service-{Guid.NewGuid():N}.db"));
        new RuntimeDatabaseMigrator(_database).Migrate();
        _repository = new SqliteFocusRepository(_database);
        _completion = new SqliteFocusCompletionUnit(
            _database,
            new SqliteEventStore(_database),
            new SqliteTimelineRepository(_database),
            new SqliteBondRepository(_database),
            new DefaultBondProgressionPolicy());
        _service = new FocusSessionService(_time, new SqliteFocusSnapshotStore(_repository), _completion);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
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
    public void Tick_uses_elapsed_time_not_callback_count()
    {
        _service.Start(FocusPreset.Create(25, 5, 4), "servant-mash");
        _time.Advance(TimeSpan.FromSeconds(7));
        _service.Tick();
        Assert.Equal(1_493, _service.Current.RemainingSeconds);
    }

    [Fact]
    public void Restore_converts_an_active_snapshot_to_paused_without_offline_advance()
    {
        var active = FocusSession.Start("stored-1", "servant-mash", FocusPreset.Create(25, 5, 4), Epoch)
            with { RemainingSeconds = 1_200 };
        _repository.SaveSnapshot(active);

        _time.Advance(TimeSpan.FromHours(8));
        _service.Restore();

        Assert.Equal(FocusStatus.PausedFocus, _service.Current.Status);
        Assert.Equal(1_200, _service.Current.RemainingSeconds);
    }

    [Fact]
    public void Restore_with_no_stored_session_stays_idle()
    {
        _service.Restore();
        Assert.Equal(FocusStatus.Idle, _service.Current.Status);
    }

    [Fact]
    public void Paused_snapshot_restores_as_paused()
    {
        var paused = FocusSession.Start("stored-1", "servant-mash", FocusPreset.Create(25, 5, 4), Epoch)
            .RestorePaused() with { RemainingSeconds = 900 };
        _repository.SaveSnapshot(paused);

        _time.Advance(TimeSpan.FromHours(2));
        _service.Restore();

        Assert.Equal(FocusStatus.PausedFocus, _service.Current.Status);
        Assert.Equal(900, _service.Current.RemainingSeconds);
    }

    [Fact]
    public void Resume_uses_a_fresh_monotonic_baseline()
    {
        _service.Start(FocusPreset.Create(25, 5, 4), "servant-mash");
        _time.Advance(TimeSpan.FromSeconds(10));
        _service.Tick();
        _service.Pause();
        _time.Advance(TimeSpan.FromMinutes(5));
        _service.Resume();
        _time.Advance(TimeSpan.FromSeconds(2));
        _service.Tick();

        Assert.Equal(1_488, _service.Current.RemainingSeconds);
    }

    [Fact]
    public void Focus_boundary_persists_the_completion_transaction_and_starts_break()
    {
        _service.Start(FocusPreset.Create(25, 5, 4), "servant-mash");
        _time.Advance(TimeSpan.FromMinutes(25));
        _service.Tick();

        Assert.Equal(FocusStatus.Breaking, _service.Current.Status);
        Assert.Equal(300, _service.Current.RemainingSeconds);
        var stored = _repository.LoadCurrent();
        Assert.NotNull(stored);
        Assert.Equal(FocusStatus.Breaking, stored!.Status);
    }

    [Fact]
    public void Stop_resets_to_idle_and_no_longer_takes_time()
    {
        _service.Start(FocusPreset.Create(25, 5, 4), "servant-mash");
        _time.Advance(TimeSpan.FromMinutes(10));
        _service.Tick();
        _service.Stop();

        Assert.Equal(FocusStatus.Idle, _service.Current.Status);
        _time.Advance(TimeSpan.FromMinutes(30));
        _service.Tick();
        Assert.Equal(FocusStatus.Idle, _service.Current.Status);
    }

    [Fact]
    public void Snapshots_are_saved_every_30_consumed_seconds_while_running()
    {
        _service.Start(FocusPreset.Create(25, 5, 4), "servant-mash");

        _time.Advance(TimeSpan.FromSeconds(31));
        _service.Tick();
        var persisted = _repository.LoadCurrent();
        Assert.NotNull(persisted);
        Assert.Equal(1_469, persisted!.RemainingSeconds);

        // Between cadence windows nothing new persists.
        _time.Advance(TimeSpan.FromSeconds(2));
        _service.Tick();
        var stale = _repository.LoadCurrent();
        Assert.Equal(1_469, stale!.RemainingSeconds);
    }

    [Fact]
    public void Persistence_failure_pauses_and_raises_the_event_until_retry()
    {
        var failing = new ThrowingSnapshotStore();
        var service = new FocusSessionService(_time, failing, _completion);
        var persistenceFailures = 0;
        service.PersistenceFailed += (_, _) => persistenceFailures++;

        service.Start(FocusPreset.Create(25, 5, 4), "servant-mash");
        Assert.Equal(1, persistenceFailures);
        Assert.Equal(FocusStatus.PausedFocus, service.Current.Status);

        // While paused-with-persistence-failure, elapsed time is not applied.
        _time.Advance(TimeSpan.FromMinutes(5));
        service.Tick();
        Assert.Equal(1_500, service.Current.RemainingSeconds);

        // Retry from the paused state succeeds (nothing to save for a paused transition).
        failing.Throw = false;
        service.Resume();
        _time.Advance(TimeSpan.FromSeconds(5));
        service.Tick();
        Assert.Equal(1_495, service.Current.RemainingSeconds);
    }

    [Fact]
    public void SnapshotChanged_fires_on_commands_and_boundaries()
    {
        var changes = 0;
        _service.SnapshotChanged += (_, _) => changes++;
        _service.Start(FocusPreset.Create(25, 5, 4), "servant-mash");
        _time.Advance(TimeSpan.FromSeconds(5));
        _service.Tick();
        _service.Pause();
        _service.Resume();
        _service.Stop();

        Assert.True(changes >= 4);
    }

    [Fact]
    public void FocusChanged_publishes_the_current_focus_snapshot()
    {
        FocusSnapshot? published = null;
        _service.FocusChanged += (_, args) => published = args.State;

        _service.Start(FocusPreset.Create(25, 5, 4), "servant-mash");

        Assert.NotNull(published);
        Assert.Equal(_service.Current, published!.Session);
    }

    [Fact]
    public void Custom_preset_persists_only_when_valid_and_round_trips()
    {
        var repository = _repository;
        repository.SavePreset(new StoredFocusPreset(
            SqliteFocusRepository.CustomPresetId, "custom", 45 * 60, 9 * 60, 3, Epoch));

        var stored = repository.LoadPreset(SqliteFocusRepository.CustomPresetId);
        Assert.NotNull(stored);
        Assert.Equal(2_700, stored!.FocusSeconds);
        Assert.Equal(540, stored.BreakSeconds);
        Assert.Equal(3, stored.Cycles);

        // Out-of-bounds custom values are rejected by the preset factory, never saved.
        Assert.Throws<ArgumentOutOfRangeException>(() => FocusPreset.Create(4, 5, 2));
    }

    [Fact]
    public void Start_assigns_a_stable_session_id_once_per_session()
    {
        string? first = null;
        _service.Start(FocusPreset.Create(25, 5, 4), "servant-mash");
        first = _service.Current.SessionId;
        Assert.False(string.IsNullOrEmpty(first));
        Assert.Equal(first, _repository.LoadCurrent()!.SessionId);
    }

    private sealed class ThrowingSnapshotStore : IFocusSnapshotStore
    {
        public bool Throw { get; set; } = true;

        public void SaveSnapshot(FocusSession session)
        {
            if (Throw)
            {
                throw new InvalidOperationException("simulated disk failure");
            }
        }

        public FocusSession? LoadCurrent() => null;
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => Now;

        public override long GetTimestamp() => Now.UtcTicks;

        public new TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) =>
            new TimeSpan(endingTimestamp - startingTimestamp);

        public void Advance(TimeSpan delta) => Now += delta;
    }
}
