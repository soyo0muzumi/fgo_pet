using System.Windows;
using Point = System.Windows.Point;

namespace FgoPet.App.Windowing;

public enum GestureEvent
{
    None,
    Click,
    DragStart,
    DragEnd,
}

/// <summary>
/// Turns press/move/release into a click or a drag using the Windows system drag
/// threshold: an exact-threshold move still counts as a click; an excess move is a drag.
/// Secondary-button presses are ignored so a right-click outside a hit region does nothing.
/// </summary>
public sealed class PointerGestureRecognizer
{
    public const int DefaultSystemDragThreshold = 4;

    private readonly int _dragThreshold;
    private Point? _pressOrigin;
    private bool _dragging;

    public PointerGestureRecognizer(int dragThreshold = DefaultSystemDragThreshold)
    {
        if (dragThreshold < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dragThreshold));
        }
        _dragThreshold = dragThreshold;
    }

    public GestureEvent Press(Point point, bool isSecondary)
    {
        if (isSecondary)
        {
            Cancel();
            return GestureEvent.None;
        }

        _pressOrigin = point;
        _dragging = false;
        return GestureEvent.None;
    }

    public GestureEvent Move(Point point)
    {
        var origin = _pressOrigin;
        if (origin is null)
        {
            return GestureEvent.None;
        }

        if (!_dragging && ExceedsThreshold(origin.Value, point))
        {
            _dragging = true;
            return GestureEvent.DragStart;
        }

        return GestureEvent.None;
    }

    public GestureEvent Release(Point point)
    {
        var origin = _pressOrigin;
        _pressOrigin = null;

        if (origin is null)
        {
            return GestureEvent.None;
        }

        if (_dragging)
        {
            _dragging = false;
            return GestureEvent.DragEnd;
        }

        return ExceedsThreshold(origin.Value, point) ? GestureEvent.DragEnd : GestureEvent.Click;
    }

    public void Cancel()
    {
        _pressOrigin = null;
        _dragging = false;
    }

    private bool ExceedsThreshold(Point from, Point to)
    {
        var dx = Math.Abs(to.X - from.X);
        var dy = Math.Abs(to.Y - from.Y);
        return dx > _dragThreshold || dy > _dragThreshold;
    }
}