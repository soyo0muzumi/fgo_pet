using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows.Automation;

namespace FgoPet.Platform.Windows.WindowText;

/// <summary>
/// Authorizes UIA nodes within one selected native window. A foreign process is not an
/// authority by itself: every check must resolve a native descendant of that window.
/// This class queries identity and structure only, never accessible names or content.
/// </summary>
internal sealed class WindowTextNativeScope
{
    private const int MaxParentNodes = 32;
    private readonly IntPtr rootHandle;
    private readonly int rootPid;
    private readonly IWindowTextScopeNative native;
    private readonly long? rootStartedUtc;
    private readonly IntPtr contentRoot;
    private readonly int contentPid;
    private readonly long? contentStartedUtc;
    private readonly Dictionary<int, long> observedProcesses = [];
    private readonly object processGate = new();

    internal WindowTextNativeScope(IntPtr rootHandle, int rootPid)
        : this(rootHandle, rootPid, new WindowsScopeNative()) { }

    internal WindowTextNativeScope(IntPtr rootHandle, int rootPid, IntPtr contentRoot)
        : this(rootHandle, rootPid, new WindowsScopeNative(), contentRoot) { }

    internal WindowTextNativeScope(IntPtr rootHandle, int rootPid, IWindowTextScopeNative native, IntPtr contentRoot = default)
    {
        this.rootHandle = rootHandle;
        this.rootPid = rootPid;
        this.native = native;
        this.contentRoot = contentRoot == rootHandle ? IntPtr.Zero : contentRoot;
        try
        {
            if (rootHandle != IntPtr.Zero && rootPid > 0 && native.IsWindow(rootHandle)
                && native.GetProcessId(rootHandle) == rootPid)
            {
                var started = native.GetProcessStartedUtc(rootPid);
                if (started > 0) rootStartedUtc = started;
            }
            if (this.contentRoot != IntPtr.Zero && native.IsWindow(this.contentRoot)
                && native.IsWindowVisible(this.contentRoot) && native.IsChild(rootHandle, this.contentRoot))
            {
                contentPid = native.GetProcessId(this.contentRoot);
                if (contentPid > 0)
                {
                    var started = native.GetProcessStartedUtc(contentPid);
                    if (started > 0) contentStartedUtc = started;
                }
            }
        }
        catch (Exception exception) when (IsIdentityFailure(exception)) { }
    }

    internal bool Allows(AutomationElement element) =>
        element is not null && Allows(new AutomationScopeNode(element));

    internal bool Allows(IWindowTextScopeNode element)
    {
        try
        {
            if (!RootMatches()) return false;
            var processId = element.ProcessId;
            if (processId <= 0) return false;
            // The collector still verifies tree membership and password/visibility boundaries.
            if (processId == rootPid) return RootMatches();
            IWindowTextScopeNode? current = element;
            for (var depth = 0; current is not null && depth < MaxParentNodes; depth++)
            {
                if (current.ProcessId != processId) return false;
                var handle = current.NativeWindowHandle;
                if (handle != IntPtr.Zero)
                {
                    // Never skip an unrelated nearest HWND to seek a more permissive ancestor.
                    if (!IsOwnedChild(handle, processId) || !RememberProcess(processId)) return false;
                    return RootMatches() && IsOwnedChild(handle, processId) && RememberProcess(processId);
                }
                if (depth + 1 == MaxParentNodes) return false;
                current = current.Parent;
            }
            return false;
        }
        catch (Exception exception) when (IsIdentityFailure(exception)) { return false; }
    }

    private bool RootMatches() => rootStartedUtc.HasValue && native.IsWindow(rootHandle)
        && native.GetProcessId(rootHandle) == rootPid
        && native.GetProcessStartedUtc(rootPid) == rootStartedUtc.Value
        // A same-process viewport can be reparented too. Revalidate this anchor on every check.
        && (contentRoot == IntPtr.Zero || (contentStartedUtc.HasValue && native.IsWindow(contentRoot)
            && native.IsWindowVisible(contentRoot) && native.IsChild(rootHandle, contentRoot)
            && native.GetProcessId(contentRoot) == contentPid
            && native.GetProcessStartedUtc(contentPid) == contentStartedUtc.Value));

    private bool IsOwnedChild(IntPtr handle, int processId) => handle != rootHandle
        && native.IsWindow(handle) && native.GetProcessId(handle) == processId
        && native.IsChild(rootHandle, handle);

    private bool RememberProcess(int processId)
    {
        var started = native.GetProcessStartedUtc(processId);
        if (started <= 0) return false;
        lock (processGate)
        {
            if (observedProcesses.TryGetValue(processId, out var prior)) return prior == started;
            observedProcesses.Add(processId, started);
            return true;
        }
    }

    private static bool IsIdentityFailure(Exception exception) => exception is ElementNotAvailableException
        or InvalidOperationException or COMException or UnauthorizedAccessException or NotSupportedException
        or ArgumentException or Win32Exception or SecurityException or OverflowException;

    private sealed class AutomationScopeNode(AutomationElement element) : IWindowTextScopeNode
    {
        public int ProcessId => element.Current.ProcessId;
        public IntPtr NativeWindowHandle => new(element.Current.NativeWindowHandle);
        public IWindowTextScopeNode? Parent => TreeWalker.RawViewWalker.GetParent(element) is { } parent
            ? new AutomationScopeNode(parent) : null;
    }

    private sealed class WindowsScopeNative : IWindowTextScopeNative
    {
        public bool IsWindow(IntPtr handle) => NativeMethods.IsWindow(handle);
        public bool IsWindowVisible(IntPtr handle) => NativeMethods.IsWindowVisible(handle);
        public bool IsChild(IntPtr parent, IntPtr child) => NativeMethods.IsChild(parent, child);
        public int GetProcessId(IntPtr handle)
        {
            NativeMethods.GetWindowThreadProcessId(handle, out var processId);
            return checked((int)processId);
        }
        public long GetProcessStartedUtc(int processId)
        {
            using var process = Process.GetProcessById(processId);
            return process.StartTime.ToUniversalTime().Ticks;
        }
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindow(IntPtr handle);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindowVisible(IntPtr handle);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsChild(IntPtr parent, IntPtr child);
        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
    }
}

internal interface IWindowTextScopeNode
{
    int ProcessId { get; }
    IntPtr NativeWindowHandle { get; }
    IWindowTextScopeNode? Parent { get; }
}

internal interface IWindowTextScopeNative
{
    bool IsWindow(IntPtr handle);
    bool IsWindowVisible(IntPtr handle);
    int GetProcessId(IntPtr handle);
    bool IsChild(IntPtr parent, IntPtr child);
    long GetProcessStartedUtc(int processId);
}
