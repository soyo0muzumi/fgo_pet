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
    public void Reopen_ignores_saved_location_and_uses_current_portrait()
    {
        StaRun(() =>
        {
            var store = new MemorySlotStore
            {
                Dialogue = new WindowPlacement("display", 50, 80, 1, 1, 480, 600),
            };
            var coordinator = new DialogueWindowPlacementCoordinator(store, new FixedScreenService());
            var window = new Window { Width = 480, Height = 600 };
            var portrait = new DeviceRect(700, 200, 180, 450);

            coordinator.ApplyOnOpen(window, portrait);

            Assert.True(window.Left >= portrait.Right);
            Assert.Equal(480, window.Width);
        });
    }

    [Fact]
    public void Reopen_follows_the_portrait_after_it_moves()
    {
        StaRun(() =>
        {
            var store = new MemorySlotStore();
            var coordinator = new DialogueWindowPlacementCoordinator(store, new FixedScreenService());
            var firstWindow = new Window { Width = 480, Height = 600 };
            var firstPortrait = new DeviceRect(100, 200, 180, 450);
            var movedWindow = new Window { Width = 480, Height = 600 };
            var movedPortrait = new DeviceRect(1000, 200, 180, 450);

            coordinator.ApplyOnOpen(firstWindow, firstPortrait);
            coordinator.ApplyOnOpen(movedWindow, movedPortrait);

            Assert.True(firstWindow.Left < movedWindow.Left);
            Assert.True(movedWindow.Left >= movedPortrait.Right);
        });
    }

    [Fact]
    public void Saving_does_not_write_any_placement_slot()
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

            Assert.Null(store.Dialogue);
            Assert.Null(store.Portrait);
        });
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void Placement_constrains_window_size_and_bounds_at_monitor_dpi(double scale)
    {
        StaRun(() =>
        {
            var workArea = new DeviceRect(-1920, 0, 1600, 900);
            var screen = new FixedScreenService(workArea, new Dpi2(scale, scale));
            var coordinator = new DialogueWindowPlacementCoordinator(new MemorySlotStore(), screen);
            var window = new Window { Width = 760, Height = 560 };
            var portrait = new DeviceRect(-1800, 250, 300, 450);

            coordinator.ApplyOnOpen(window, portrait);

            var actual = new DeviceRect(
                (int)Math.Round(window.Left * scale),
                (int)Math.Round(window.Top * scale),
                (int)Math.Round(window.Width * scale),
                (int)Math.Round(window.Height * scale));
            Assert.True(window.Width <= workArea.Width / scale);
            Assert.True(window.Height <= workArea.Height / scale);
            Assert.True(actual.X >= workArea.X);
            Assert.True(actual.Y >= workArea.Y);
            Assert.True(actual.Right <= workArea.Right);
            Assert.True(actual.Bottom <= workArea.Bottom);
        });
    }

    [Fact]
    public void Oversized_window_is_resized_to_the_work_area()
    {
        StaRun(() =>
        {
            var workArea = new DeviceRect(0, 0, 160, 120);
            var coordinator = new DialogueWindowPlacementCoordinator(
                new MemorySlotStore(),
                new FixedScreenService(workArea));
            var window = new Window { Width = 760, Height = 560, MinWidth = 320, MinHeight = 240 };

            coordinator.ApplyOnOpen(window, new DeviceRect(20, 20, 30, 50));
            window.Show();
            try
            {
                window.UpdateLayout();

                Assert.True(window.ActualWidth <= workArea.Width);
                Assert.True(window.ActualHeight <= workArea.Height);
                Assert.True(window.Left >= workArea.X);
                Assert.True(window.Top >= workArea.Y);
                Assert.True(window.Left + window.ActualWidth <= workArea.Right);
                Assert.True(window.Top + window.ActualHeight <= workArea.Bottom);
            }
            finally
            {
                window.Hide();
            }
        });
    }

    [Fact]
    public void Constrained_minimum_and_size_restore_when_reopened_on_a_larger_work_area()
    {
        StaRun(() =>
        {
            var screen = new SwitchableScreenService(new DeviceRect(0, 0, 160, 120));
            var coordinator = new DialogueWindowPlacementCoordinator(new MemorySlotStore(), screen);
            var window = new Window { Width = 760, Height = 560, MinWidth = 320, MinHeight = 240 };

            coordinator.ApplyOnOpen(window, new DeviceRect(20, 20, 30, 50));
            Assert.Equal(160, window.Width);
            Assert.Equal(120, window.Height);
            Assert.Equal(160, window.MinWidth);
            Assert.Equal(120, window.MinHeight);

            screen.WorkArea = new DeviceRect(0, 0, 1200, 900);
            coordinator.ApplyOnOpen(window, new DeviceRect(600, 200, 30, 50));

            Assert.Equal(760, window.Width);
            Assert.Equal(560, window.Height);
            Assert.Equal(320, window.MinWidth);
            Assert.Equal(240, window.MinHeight);
        });
    }

    [Fact]
    public void Expanded_or_user_resized_size_survives_a_later_reopen()
    {
        StaRun(() =>
        {
            var screen = new SwitchableScreenService(new DeviceRect(0, 0, 160, 120));
            var coordinator = new DialogueWindowPlacementCoordinator(new MemorySlotStore(), screen);
            var window = new Window { Width = 760, Height = 560, MinWidth = 320, MinHeight = 240 };

            coordinator.ApplyOnOpen(window, new DeviceRect(20, 20, 30, 50));
            screen.WorkArea = new DeviceRect(0, 0, 1600, 1200);
            coordinator.ApplyOnOpen(window, new DeviceRect(600, 200, 30, 50));

            window.Width = 980;
            window.Height = 720;
            window.MinWidth = 420;
            window.MinHeight = 300;
            coordinator.ApplyOnOpen(window, new DeviceRect(600, 200, 30, 50));
            coordinator.ApplyOnOpen(window, new DeviceRect(700, 250, 30, 50));

            Assert.Equal(980, window.Width);
            Assert.Equal(720, window.Height);
            Assert.Equal(420, window.MinWidth);
            Assert.Equal(300, window.MinHeight);
        });
    }

    [Fact]
    public void Invalid_dpi_leaves_window_unchanged()
    {
        StaRun(() =>
        {
            var coordinator = new DialogueWindowPlacementCoordinator(
                new MemorySlotStore(),
                new FixedScreenService(dpi: new Dpi2(0, double.NaN)));
            var window = new Window { Left = 30, Top = 40, Width = 760, Height = 560 };

            coordinator.ApplyOnOpen(window, new DeviceRect(100, 200, 30, 50));

            Assert.Equal(30, window.Left);
            Assert.Equal(40, window.Top);
            Assert.Equal(760, window.Width);
            Assert.Equal(560, window.Height);
        });
    }

    [Fact]
    public void Missing_monitors_leaves_window_unchanged()
    {
        StaRun(() =>
        {
            var coordinator = new DialogueWindowPlacementCoordinator(new MemorySlotStore(), new EmptyScreenService());
            var window = new Window { Left = 30, Top = 40, Width = 760, Height = 560 };

            coordinator.ApplyOnOpen(window, new DeviceRect(100, 200, 30, 50));

            Assert.Equal(30, window.Left);
            Assert.Equal(40, window.Top);
            Assert.Equal(760, window.Width);
            Assert.Equal(560, window.Height);
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

    private sealed class FixedScreenService(DeviceRect? workArea = null, Dpi2? dpi = null) : IScreenLayoutService
    {
        private readonly DeviceRect _workArea = workArea ?? new DeviceRect(0, 0, 2000, 1200);
        private readonly Dpi2 _dpi = dpi ?? new Dpi2(1, 1);

        public IReadOnlyList<MonitorInfo> GetMonitors() => [new MonitorInfo("display", _workArea, true)];
        public Dpi2 GetDpi(string monitorId) => _dpi;
    }

    private sealed class EmptyScreenService : IScreenLayoutService
    {
        public IReadOnlyList<MonitorInfo> GetMonitors() => [];
        public Dpi2 GetDpi(string monitorId) => new(1, 1);
    }

    private sealed class SwitchableScreenService(DeviceRect initialWorkArea) : IScreenLayoutService
    {
        public DeviceRect WorkArea { get; set; } = initialWorkArea;
        public IReadOnlyList<MonitorInfo> GetMonitors() => [new MonitorInfo("display", WorkArea, true)];
        public Dpi2 GetDpi(string monitorId) => new(1, 1);
    }

    private static void StaRun(Action action) => StaRunner.Run(action);
}
