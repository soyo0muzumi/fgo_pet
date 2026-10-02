using System.Windows;
using System.Windows.Controls;
using FgoPet.App.Windowing;
using FgoPet.Extensibility;
using Xunit;

namespace FgoPet.Windows.Tests.Shell;

public sealed class TransientSurfaceWindowTests
{
    [Fact]
    public void A_separate_owned_window_disposes_its_surface_without_changing_owner_bounds()
    {
        StaRunner.Run(() =>
        {
            var owner = new Window { Width = 300, Height = 350, ShowInTaskbar = false,
                Left = -10000, Top = -10000 };
            owner.Show();
            var view = new DisposableView();
            var window = new TransientSurfaceWindow(owner,
                new TransientSurfaceDescriptor("test.peek", "Peek", 320, 400), view);

            Assert.Same(owner, window.Owner);
            Assert.Same(view, window.Content);
            Assert.Equal(320, window.Width);
            Assert.Equal(400, window.Height);
            Assert.Equal(300, owner.Width);
            Assert.Equal(350, owner.Height);

            window.Show();
            window.Close();
            Assert.True(view.Disposed);
            owner.Close();
        });
    }

    private sealed class DisposableView : Border, IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
}
