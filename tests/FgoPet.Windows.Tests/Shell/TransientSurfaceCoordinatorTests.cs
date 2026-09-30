using System.Windows;
using System.Windows.Controls;
using FgoPet.App.Windowing;
using FgoPet.Core.Geometry;
using FgoPet.Core.Windowing;
using FgoPet.Extensibility;
using FgoPet.UiSdk;
using Xunit;

namespace FgoPet.Windows.Tests.Shell;

public sealed class TransientSurfaceCoordinatorTests
{
    [Fact]
    public void Toggle_opens_a_separate_surface_and_disposes_it_on_the_second_click()
    {
        StaRunner.Run(() =>
        {
            var owner = new Window { Width = 300, Height = 350, ShowInTaskbar = false,
                Left = -10000, Top = -10000 };
            owner.Show();
            var catalog = new FakeCatalog();
            using var coordinator = new TransientSurfaceCoordinator(catalog, new Screen(),
                () => owner, () => new DeviceRect(1200, 300, 300, 350));

            Assert.True(coordinator.Toggle("test.peek"));
            Assert.True(coordinator.IsOpen);
            Assert.Equal(300, owner.Width);
            Assert.True(coordinator.Toggle("test.peek"));
            Assert.False(coordinator.IsOpen);
            Assert.True(catalog.Created!.Disposed);
            owner.Close();
        });
    }

    private sealed class FakeCatalog : ITransientSurfaceCatalog
    {
        public DisposableView? Created { get; private set; }
        public IReadOnlyList<TransientSurfaceDescriptor> TransientSurfaces { get; } =
            [new("test.peek", "Peek", 320, 400)];
        public FrameworkElement CreateView(string surfaceId) => Created = new DisposableView();
    }

    private sealed class DisposableView : Border, IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    private sealed class Screen : IScreenLayoutService
    {
        public IReadOnlyList<MonitorInfo> GetMonitors() => [new("fixture", new DeviceRect(0, 0, 1920, 1080), true)];
        public Dpi2 GetDpi(string monitorId) => new(1, 1);
    }
}
