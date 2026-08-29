using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using FgoPet.App.Windowing;
using Xunit;

namespace FgoPet.App.Tests.Windowing;

public sealed class InteractiveSurfaceTests
{
    [Fact]
    public void Input_controls_and_their_descendants_are_interactive()
    {
        StaRun(() =>
        {
            var textBox = new TextBox();
            var button = new Button();
            var scrollBar = new ScrollBar();

            Assert.True(InteractiveSurface.Contains(textBox));
            Assert.True(InteractiveSurface.Contains(button));
            Assert.True(InteractiveSurface.Contains(scrollBar));
        });
    }

    [Fact]
    public void Plain_panel_background_remains_a_drag_surface()
    {
        StaRun(() => Assert.False(InteractiveSurface.Contains(new Grid())));
    }

    private static void StaRun(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
