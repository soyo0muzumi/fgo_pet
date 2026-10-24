using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using FgoPet.Platform.WindowText;

namespace FgoPet.Platform.Windows.WindowText;

/// <summary>Locates one visible Chromium viewport inside the selected window, using metadata only.</summary>
internal static class WindowTextReadRoot
{
    internal static IntPtr Select(IntPtr selectedRoot, CancellationToken token = default) =>
        Select(selectedRoot, new WindowsReadRootNative(), token);

    internal static IntPtr Select(IntPtr selectedRoot, IWindowTextReadRootNative native, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        // A null parent means the entire desktop to EnumChildWindows; never enumerate it here.
        if (selectedRoot == IntPtr.Zero) return selectedRoot;
        var visited = 0;
        var matches = 0;
        var failed = false;
        var result = selectedRoot;
        var complete = native.VisitChildren(selectedRoot, child =>
        {
            // Do not throw through the unmanaged enumeration callback.
            if (token.IsCancellationRequested) return false;
            if (++visited > WindowTextLimits.MaxNodes) return false;
            try
            {
                if (child == selectedRoot || !native.IsChild(selectedRoot, child) || !native.IsVisible(child)
                    || native.GetClassName(child) != "Chrome_RenderWidgetHostHWND") return true;
                result = child;
                return ++matches == 1;
            }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException
                or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                failed = true;
                return false;
            }
        });
        token.ThrowIfCancellationRequested();
        // An incomplete/ambiguous search never guesses a viewport. Use the original bounded reader.
        return complete && !failed && visited <= WindowTextLimits.MaxNodes && matches == 1 ? result : selectedRoot;
    }

    private sealed class WindowsReadRootNative : IWindowTextReadRootNative
    {
        public bool VisitChildren(IntPtr root, Func<IntPtr, bool> visitor)
        {
            var complete = true;
            // EnumChildWindows has no documented useful return value; track our own early stop.
            NativeMethods.EnumChildWindows(root, (handle, _) => complete = visitor(handle), IntPtr.Zero);
            return complete;
        }
        public bool IsVisible(IntPtr handle) => NativeMethods.IsWindowVisible(handle);
        public bool IsChild(IntPtr root, IntPtr child) => NativeMethods.IsChild(root, child);
        public string GetClassName(IntPtr handle)
        {
            var name = new StringBuilder(256);
            return NativeMethods.GetClassName(handle, name, name.Capacity) > 0 ? name.ToString() : string.Empty;
        }
    }

    private static class NativeMethods
    {
        internal delegate bool EnumChildCallback(IntPtr handle, IntPtr parameter);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumChildWindows(IntPtr parent, EnumChildCallback callback, IntPtr parameter);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindowVisible(IntPtr handle);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsChild(IntPtr parent, IntPtr child);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetClassName(IntPtr handle, StringBuilder value, int maximum);
    }
}

internal interface IWindowTextReadRootNative
{
    bool VisitChildren(IntPtr root, Func<IntPtr, bool> visitor);
    bool IsVisible(IntPtr handle);
    bool IsChild(IntPtr root, IntPtr child);
    string GetClassName(IntPtr handle);
}
