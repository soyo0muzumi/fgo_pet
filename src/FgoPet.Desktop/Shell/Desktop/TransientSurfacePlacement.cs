using FgoPet.Core.Geometry;
using FgoPet.Core.Windowing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace FgoPet.App.Windowing;

internal static class TransientSurfacePlacement
{
    internal static MonitorInfo? SelectMonitor(DeviceRect anchor, IReadOnlyList<MonitorInfo> monitors) =>
        monitors.Where(item => item.WorkArea.Width > 0 && item.WorkArea.Height > 0)
            .OrderByDescending(item => (long)Math.Max(0, Math.Min(anchor.Right,item.WorkArea.Right)-Math.Max(anchor.Left,item.WorkArea.Left))
                * Math.Max(0, Math.Min(anchor.Bottom,item.WorkArea.Bottom)-Math.Max(anchor.Top,item.WorkArea.Top)))
            .ThenBy(item => DistanceSquared(anchor,item.WorkArea))
            .ThenByDescending(item => item.IsPrimary).FirstOrDefault();

    private static double DistanceSquared(DeviceRect anchor, DeviceRect area)
    {
        var x=anchor.X+anchor.Width/2d; var y=anchor.Y+anchor.Height/2d;
        var dx=x-Math.Clamp(x,area.Left,area.Right); var dy=y-Math.Clamp(y,area.Top,area.Bottom);
        return dx*dx+dy*dy;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out NativeRect rect);

    internal static bool ContainsScreenPoint(Window? window, Point point)
    {
        if (window?.IsVisible != true || window.WindowState == WindowState.Minimized) return false;
        return GetWindowRect(new WindowInteropHelper(window).Handle, out var rect)
            && point.X >= rect.Left && point.X < rect.Right && point.Y >= rect.Top && point.Y < rect.Bottom;
    }

    internal static bool TryReadNativeBounds(Window window, out DeviceRect bounds)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle != 0 && GetWindowRect(handle, out var rect))
        {
            bounds = new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
            return bounds.Width > 0 && bounds.Height > 0;
        }
        bounds = default;
        return false;
    }

    internal static void ApplyNativeBounds(Window window, DeviceRect bounds)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle != 0 && !SetWindowPos(handle, 0, bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x0014))
            System.Diagnostics.Trace.TraceWarning("MARGIN_WINDOW_PLACEMENT_FAILED");
    }

    internal static void MoveNativeWindow(Window window, DevicePoint position)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle != 0 && !SetWindowPos(handle,0,position.X,position.Y,0,0,0x0015))
            System.Diagnostics.Trace.TraceWarning("MARGIN_WINDOW_PLACEMENT_FAILED");
    }

    public static DeviceRect Place(DeviceRect anchor, DeviceRect workArea, Dpi2 dpi,
        double widthDip, double heightDip, double marginDip = 0)
    {
        if (!double.IsFinite(dpi.X) || dpi.X <= 0 || !double.IsFinite(dpi.Y) || dpi.Y <= 0)
            throw new ArgumentOutOfRangeException(nameof(dpi));
        workArea = Inset(workArea, dpi, marginDip);
        var width = Math.Min(workArea.Width, Math.Max(1, (int)Math.Ceiling(widthDip * dpi.X)));
        var height = Math.Min(workArea.Height, Math.Max(1, (int)Math.Ceiling(heightDip * dpi.Y)));
        var gap = Math.Max(1, (int)Math.Round(16 * dpi.X));
        var right = anchor.Right + gap;
        var left = anchor.Left - gap - width;
        var x = right + width <= workArea.Right ? right
            : left >= workArea.Left ? left : right;
        return ScreenLayout.ClampFullyVisible(new DeviceRect(x, anchor.Top, width, height), workArea);
    }

    internal static DeviceRect Constrain(DeviceRect bounds, DeviceRect workArea, Dpi2 dpi,
        double widthDip, double heightDip, double marginDip = 0)
    {
        workArea = Inset(workArea, dpi, marginDip);
        var width = Math.Min(workArea.Width, Math.Max(1, (int)Math.Ceiling(widthDip * dpi.X)));
        var height = Math.Min(workArea.Height, Math.Max(1, (int)Math.Ceiling(heightDip * dpi.Y)));
        return ScreenLayout.ClampFullyVisible(new DeviceRect(bounds.X, bounds.Y, width, height), workArea);
    }

    private static DeviceRect Inset(DeviceRect workArea, Dpi2 dpi, double marginDip)
    {
        var marginX = Math.Min(Math.Max(0, (workArea.Width - 1) / 2), (int)Math.Round(marginDip * dpi.X));
        var marginY = Math.Min(Math.Max(0, (workArea.Height - 1) / 2), (int)Math.Round(marginDip * dpi.Y));
        return new DeviceRect(workArea.X + marginX, workArea.Y + marginY,
            workArea.Width - 2 * marginX, workArea.Height - 2 * marginY);
    }

}
