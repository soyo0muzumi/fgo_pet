using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using FgoPet.App.Windowing;
using FgoPet.Core.Geometry;
using FgoPet.Core.Windowing;
using Xunit;

namespace FgoPet.Windows.Tests.Windowing;

[Trait("Category", "WindowsIntegration")]
public sealed class DialogueWindowPlacementTests
{
    [Fact]
    public void First_open_defaults_to_the_right_of_the_portrait_without_overlapping_it()
    {
        StaRun(() =>
        {
            var store = new MemorySlotStore();
            var coordinator = new DialogueWindowPlacementCoordinator(store, new FixedScreenService());
            var window = new Window { Width = 760, Height = 560 };
            var portrait = new DeviceRect(100, 500, 300, 600);

            coordinator.ApplyOnOpen(window, portrait);

            Assert.True(window.Left >= portrait.X + portrait.Width);
            Assert.False(Overlaps(window, portrait));
        });
    }

    [Fact]
    public void First_open_falls_back_to_the_left_when_the_right_side_has_no_room()
    {
        StaRun(() =>
        {
            var store = new MemorySlotStore();
            var coordinator = new DialogueWindowPlacementCoordinator(store, new FixedScreenService(new DeviceRect(0, 0, 1600, 1200)));
            var window = new Window { Width = 760, Height = 560 };
            var portrait = new DeviceRect(820, 200, 300, 600);

            coordinator.ApplyOnOpen(window, portrait);

            Assert.True(window.Left + window.Width <= portrait.X);
        });
    }

    [Fact]
    public void When_neither_side_fits_the_window_stays_fully_visible_in_the_work_area()
    {
        StaRun(() =>
        {
            var store = new MemorySlotStore();
            var coordinator = new DialogueWindowPlacementCoordinator(store, new FixedScreenService(new DeviceRect(0, 0, 1100, 1200)));
            var window = new Window { Width = 760, Height = 560 };
            var portrait = new DeviceRect(100, 200, 300, 600);

            coordinator.ApplyOnOpen(window, portrait);

            Assert.True(window.Left >= 0);
            Assert.True(window.Left + window.Width <= 1100);
        });
    }

    [Fact]
    public void Saved_placement_on_a_live_monitor_restores_the_previous_bounds()
    {
        StaRun(() =>
        {
            var store = new MemorySlotStore
            {
                Dialogue = new WindowPlacement("display", 400, 100, 1, 1, 760, 560),
            };
            var coordinator = new DialogueWindowPlacementCoordinator(store, new FixedScreenService());
            var window = new Window { Width = 760, Height = 560 };

            coordinator.ApplyOnOpen(window, new DeviceRect(100, 500, 300, 600));

            Assert.Equal(400, window.Left);
            Assert.Equal(100, window.Top);
        });
    }

    [Fact]
    public void Saved_placement_on_a_gone_monitor_falls_back_to_the_portrait_side()
    {
        StaRun(() =>
        {
            var store = new MemorySlotStore
            {
                Dialogue = new WindowPlacement("display-gone", 400, 100, 1, 1, 760, 560),
            };
            var coordinator = new DialogueWindowPlacementCoordinator(store, new FixedScreenService());
            var window = new Window { Width = 760, Height = 560 };
            var portrait = new DeviceRect(100, 500, 300, 600);

            coordinator.ApplyOnOpen(window, portrait);

            Assert.True(window.Left >= portrait.X + portrait.Width);
        });
    }

    [Fact]
    public void Saving_writes_the_dialogue_slot_not_the_portrait_slot()
    {
        StaRun(() =>
        {
            var store = new MemorySlotStore();
            var coordinator = new DialogueWindowPlacementCoordinator(store, new FixedScreenService());
            var window = new Window { Left = 320, Top = 500, Width = 760, Height = 560 };

            window.Show();
            try
            {
                coordinator.SaveOnClose(window);
            }
            finally
            {
                window.Hide();
            }

            Assert.NotNull(store.Dialogue);
            Assert.Equal("display", store.Dialogue!.MonitorId);
            Assert.Equal(760, store.Dialogue.WindowWidthDip);
            Assert.Equal(320, store.Dialogue.OffsetX);
            Assert.Null(store.Portrait);
        });
    }

    private static bool Overlaps(Window window, DeviceRect portrait) =>
        window.Left < portrait.X + portrait.Width
        && window.Left + window.Width > portrait.X
        && window.Top < portrait.Y + portrait.Height
        && window.Top + window.Height > portrait.Y;

    private sealed class MemorySlotStore : IWindowPlacementStore
    {
        public string Location => "memory";
        public WindowPlacement? Portrait { get; set; }
        public WindowPlacement? Dialogue { get; set; }
        public WindowPlacement? Load() => Portrait;
        public void Save(WindowPlacement placement) => Portrait = placement;
        public WindowPlacement? Load(string slot) => slot == WindowPlacementSlots.Dialogue ? Dialogue : Portrait;
        public void Save(string slot, WindowPlacement placement)
        {
            if (slot == WindowPlacementSlots.Dialogue)
            {
                Dialogue = placement;
            }
            else
            {
                Portrait = placement;
            }
        }
    }

    private sealed class FixedScreenService(DeviceRect? workArea = null) : IScreenLayoutService
    {
        private readonly DeviceRect _workArea = workArea ?? new DeviceRect(0, 0, 2000, 1200);

        public IReadOnlyList<MonitorInfo> GetMonitors() => [new MonitorInfo("display", _workArea, true)];
        public Dpi2 GetDpi(string monitorId) => new(1, 1);
    }

    private static void StaRun(Action action) => StaRunner.Run(action);
}
