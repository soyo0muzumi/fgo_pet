using System.Runtime.InteropServices;
using FgoPet.Core.Geometry;
using FgoPet.Core.Windowing;

namespace FgoPet.Infrastructure.Windowing;

/// <summary>Enumerates monitors and their effective DPI through Win32 APIs.</summary>
public sealed class WindowsScreenLayoutService : IScreenLayoutService
{
    private const uint MonitorInfoFlagsPrimary = 1;
    private const int EffectiveDpi = 0;

    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var monitors = new List<MonitorInfo>();
        var callback = new MonitorEnumProc((IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr data) =>
        {
            var info = MonitorInfoEx.Create();
            if (GetMonitorInfo(hMonitor, ref info))
            {
                var work = info.RcWork;
                var primary = (info.DwFlags & MonitorInfoFlagsPrimary) != 0;
                monitors.Add(new MonitorInfo(
                    info.SzDevice.TrimEnd('\0'),
                    new DeviceRect(work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top),
                    primary));
            }
            return true;
        });

        if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero))
        {
            return Array.Empty<MonitorInfo>();
        }

        return monitors;
    }

    public Dpi2 GetDpi(string monitorId)
    {
        var found = IntPtr.Zero;
        var callback = new MonitorEnumProc((IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr data) =>
        {
            var info = MonitorInfoEx.Create();
            if (GetMonitorInfo(hMonitor, ref info) && info.SzDevice.TrimEnd('\0') == monitorId)
            {
                found = hMonitor;
                return false;
            }
            return true;
        });
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);

        if (found == IntPtr.Zero || GetDpiForMonitor(found, EffectiveDpi, out var dpiX, out var dpiY) != 0)
        {
            return new Dpi2(1.0, 1.0);
        }

        return new Dpi2(dpiX / 96.0, dpiY / 96.0);
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int CbSize;
        public RECT RcMonitor;
        public RECT RcWork;
        public uint DwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string SzDevice;

        public static MonitorInfoEx Create() => new()
        {
            CbSize = Marshal.SizeOf<MonitorInfoEx>(),
            SzDevice = new string('\0', 32),
        };
    }

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfoEx lpmi);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
}