using System.Windows;
using FgoPet.App.Windowing;
using Xunit;

namespace FgoPet.App.Tests.Windowing;

public sealed class PointerGestureRecognizerTests
{
    // Windows system drag thresholds are 4 px on both axes.
    private const int Threshold = 4;

    [Fact]
    public void An_exact_threshold_move_is_still_a_click()
    {
        var recognizer = new PointerGestureRecognizer(Threshold);
        recognizer.Press(new Point(0, 0), isSecondary: false);
        Assert.Equal(GestureEvent.None, recognizer.Move(new Point(Threshold, 0)));
        Assert.Equal(GestureEvent.Click, recognizer.Release(new Point(Threshold, 0)));
    }

    [Fact]
    public void A_move_beyond_the_threshold_is_a_drag()
    {
        var recognizer = new PointerGestureRecognizer(Threshold);
        recognizer.Press(new Point(0, 0), isSecondary: false);
        Assert.Equal(GestureEvent.DragStart, recognizer.Move(new Point(Threshold + 1, 0)));
        Assert.Equal(GestureEvent.DragEnd, recognizer.Release(new Point(Threshold + 1, 0)));
    }

    [Fact]
    public void Release_without_moving_is_a_click()
    {
        var recognizer = new PointerGestureRecognizer(Threshold);
        recognizer.Press(new Point(10, 10), isSecondary: false);
        Assert.Equal(GestureEvent.Click, recognizer.Release(new Point(10, 10)));
    }

    [Fact]
    public void A_secondary_press_is_ignored()
    {
        var recognizer = new PointerGestureRecognizer(Threshold);
        Assert.Equal(GestureEvent.None, recognizer.Press(new Point(5, 5), isSecondary: true));
        Assert.Equal(GestureEvent.None, recognizer.Move(new Point(50, 50)));
        Assert.Equal(GestureEvent.None, recognizer.Release(new Point(50, 50)));
    }

    [Fact]
    public void Move_and_release_without_a_press_are_none()
    {
        var recognizer = new PointerGestureRecognizer(Threshold);
        Assert.Equal(GestureEvent.None, recognizer.Move(new Point(1, 1)));
        Assert.Equal(GestureEvent.None, recognizer.Release(new Point(1, 1)));
    }

    
}