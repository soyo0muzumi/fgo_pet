using System.Windows.Threading;
using FgoPet.App.Focus;
using FgoPet.Core.Focus;
using FgoPet.Plugin.Focus.Desktop;
using Xunit;
using System.IO;
using FgoPet.Core.Bond;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Infrastructure.Focus;
using FgoPet.Infrastructure.Events;
using FgoPet.Infrastructure.Timeline;
using FgoPet.Infrastructure.Bond;

namespace FgoPet.Windows.Tests.Focus;

public sealed class FocusPluginLifetimeTests
{
    [Fact]
    public Task Signals_follow_successful_transitions_and_not_snapshot_ticks_or_failed_writes() => StaRunner.RunAsync(async () =>
    {
        var path = Path.Combine(Path.GetTempPath(), $"fgo-focus-plugin-{Guid.NewGuid():N}.db");
        try
        {
            var database = new RuntimeDatabase(path, pooling: false);
            new RuntimeDatabaseMigrator(database).Migrate();
            var clock = new ManualClock();
            var store = new TestSnapshotStore();
            var completion = new SqliteFocusCompletionUnit(database, new SqliteEventStore(database),
                new SqliteTimelineRepository(database), new SqliteBondRepository(database), new DefaultBondProgressionPolicy());
            var focus = new FocusSessionService(clock, store, completion);
            using var stopping = new CancellationTokenSource();
            await using var plugin = new FocusDesktopPlugin(focus, clock);
            var signals = new List<CompanionSignal>();
            plugin.Signal += signals.Add;
            await plugin.StartAsync(stopping.Token);
            focus.Start(FocusPreset.Create(5, 5, 1), "role");
            Assert.Equal(CompanionSignalKind.ActivityStarted, Assert.Single(signals).Kind);
            clock.Advance(TimeSpan.FromSeconds(5));
            clock.Fire();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Single(signals);
            focus.Pause();
            Assert.Equal(CompanionSignalKind.ActivityPaused, signals[1].Kind);
            store.FailWrites = true;
            focus.Resume();
            Assert.Equal(FocusStatus.PausedFocus, focus.Current.Status);
            Assert.Equal(2, signals.Count);
            store.FailWrites = false;
            stopping.Cancel();
            focus.Resume();
            Assert.Equal(FocusStatus.PausedFocus, focus.Current.Status);
            await plugin.StopAsync(default);
            Assert.Equal(focus.Current, store.Snapshot);
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }
    });

    [Fact]
    public Task Clock_runs_without_a_window_and_coalesces_pending_callbacks() => StaRunner.RunAsync(async () =>
    {
        var clock = new ManualClock();
        var focus = new CountingFocus();
        await using var plugin = new FocusDesktopPlugin(focus, clock);
        await plugin.StartAsync(default);
        clock.Fire();
        clock.Fire();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(1, focus.Ticks);
        clock.Fire();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(2, focus.Ticks);
    });

    [Fact]
    public Task Stop_waits_for_timer_disposal_and_discards_queued_ticks() => StaRunner.RunAsync(async () =>
    {
        var clock = new ManualClock { DelayDisposal = true };
        var focus = new CountingFocus();
        var plugin = new FocusDesktopPlugin(focus, clock);
        await plugin.StartAsync(default);
        clock.Fire();
        var stopping = plugin.StopAsync(default).AsTask();
        Assert.False(stopping.IsCompleted);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(0, focus.Ticks);
        clock.ReleaseDisposal();
        await stopping;
        clock.Fire();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(0, focus.Ticks);
        await plugin.DisposeAsync();
        Assert.Equal(1, clock.Disposals);
    });

    [Fact]
    public Task Process_cancellation_fences_pending_callbacks() => StaRunner.RunAsync(async () =>
    {
        using var stopping = new CancellationTokenSource();
        var clock = new ManualClock();
        var focus = new CountingFocus();
        await using var plugin = new FocusDesktopPlugin(focus, clock);
        await plugin.StartAsync(stopping.Token);
        clock.Fire();
        stopping.Cancel();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        clock.Fire();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(0, focus.Ticks);
    });

    [Fact]
    public Task Repeated_start_does_not_create_another_clock() => StaRunner.RunAsync(async () =>
    {
        var clock = new ManualClock();
        await using var plugin = new FocusDesktopPlugin(new CountingFocus(), clock);
        await plugin.StartAsync(default);
        await plugin.StartAsync(default);
        Assert.Equal(1, clock.Created);
    });

    private sealed class ManualClock : TimeProvider
    {
        private TimerCallback? _callback;
        private object? _state;
        private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Created { get; private set; }
        public int Disposals { get; private set; }
        public bool DelayDisposal { get; init; }
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(_timestamp);
        public void Advance(TimeSpan elapsed) => _timestamp += elapsed.Ticks;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(TimeSpan.FromSeconds(1), dueTime);
            Assert.Equal(dueTime, period);
            Created++;
            _callback = callback;
            _state = state;
            return new ManualTimer(this);
        }
        // Simulate a callback already captured by the timer thread, including after disposal.
        public void Fire() => _callback?.Invoke(_state);
        public void ReleaseDisposal() => _disposed.TrySetResult();
        private sealed class ManualTimer(ManualClock owner) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() => owner.Disposals++;
            public async ValueTask DisposeAsync()
            {
                owner.Disposals++;
                if (owner.DelayDisposal) await owner._disposed.Task;
            }
        }
    }

    private sealed class TestSnapshotStore : IFocusSnapshotStore
    {
        public bool FailWrites { get; set; }
        public FocusSession? Snapshot { get; private set; }
        public void SaveSnapshot(FocusSession session)
        {
            if (FailWrites) throw new IOException("test persistence failure");
            Snapshot = session;
        }
        public FocusSession? LoadCurrent() => Snapshot;
    }

    private sealed class CountingFocus : IFocusSessionService
    {
        public int Ticks { get; private set; }
        public FocusSession Current => FocusSession.Idle;
        public event EventHandler? SnapshotChanged { add { } remove { } }
        public event EventHandler? PersistenceFailed { add { } remove { } }
        public void Start(FocusPreset preset, string servantId) { }
        public void Pause() { }
        public void Resume() { }
        public void Stop() { }
        public void Tick() => Ticks++;
        public void Restore() { }
    }
}
