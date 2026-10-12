using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FgoPet.Plugin.DesktopContext;

/// <summary>No titles, paths, command lines, UI text or screenshots. Not registered by the production host.</summary>
public static class WindowsApplicationDiscovery
{
    public static ApplicationSnapshot Read(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var started = DateTimeOffset.UtcNow;
        var elapsed = Stopwatch.StartNew();
        var foreground = GetForegroundWindow();
        var shell = GetShellWindow();
        var applications = new Dictionary<string, OpenApplication>(StringComparer.OrdinalIgnoreCase);
        var scanned = 0;
        var partial = false;
        var foregroundObserved = false;
        var completed = EnumWindows((window, _) =>
        {
            if (token.IsCancellationRequested || ++scanned > 512 || elapsed.Elapsed > TimeSpan.FromSeconds(2))
            { partial = true; return false; }
            if (window == shell || !IsWindowVisible(window)) return true;
            if (DwmGetWindowAttribute(window, 14, out var cloaked, sizeof(int)) != 0)
            { partial = true; return true; }
            if (cloaked != 0) return true;
            var application = ReadWindow(window, foreground, out var unavailable);
            partial |= unavailable;
            if (application is null) return true;
            foregroundObserved |= application.IsForeground;
            if (applications.TryGetValue(application.Name, out var previous))
                applications[application.Name] = previous with { WindowCount = previous.WindowCount + 1,
                    IsForeground = previous.IsForeground || application.IsForeground };
            else if (applications.Count < 32) applications.Add(application.Name, application);
            else partial = true;
            return true;
        }, 0);
        partial |= !completed;
        token.ThrowIfCancellationRequested();
        return new(applications.Values.OrderByDescending(a => a.IsForeground).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
            partial, foregroundObserved && applications.Values.Any(a => a.IsForeground), started, DateTimeOffset.UtcNow);
    }

    internal static OpenApplication? ReadWindow(nint window, nint foreground, out bool unavailable)
    {
        unavailable = false;
        if (GetWindowThreadProcessId(window, out var processId) == 0 || processId == 0) { unavailable = true; return null; }
        try
        {
            using var process = Process.GetProcessById(checked((int)processId));
            return new(process.ProcessName, 1, window == foreground);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception or OverflowException)
        { unavailable = true; return null; }
    }

    private delegate bool EnumWindowCallback(nint window, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);
}
