using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Automation;
using FgoPet.Platform.WindowText;

namespace FgoPet.Platform.Windows.WindowText;

/// <summary>
/// Native picker port. All content reads occur on one MTA worker, from the selected root only.
/// A caller deadline cancels between UIA calls; an unresponsive synchronous provider cannot be
/// forcibly interrupted. The process-wide gate prevents abandoned reads accumulating threads.
/// </summary>
public sealed class WindowsWindowTextReader(IWindowTextDiagnostics? diagnostics = null) : IWindowTextReader, IDisposable
{
    private static readonly SemaphoreSlim WorkerGate = new(1, 1);
    private static readonly HashSet<string> ExcludedProcesses = new(StringComparer.OrdinalIgnoreCase)
        { "CredentialUIBroker", "consent", "LogonUI", "LockApp", "lsass", "winlogon", "SearchHost", "StartMenuExperienceHost" };
    private readonly Dictionary<string, NativeCandidate> candidates = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource lifetime = new();
    private bool disposed;

    public IReadOnlyList<WindowTextCandidate> ListCandidates()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var next = new Dictionary<string, NativeCandidate>(StringComparer.Ordinal);
        var visible = new List<WindowTextCandidate>();
        var inspected = 0;
        NativeMethods.EnumWindows((handle, _) =>
        {
            if (++inspected > 1024 || visible.Count >= WindowTextLimits.MaxCandidates) return false;
            if (!TryDescribe(handle, out var native)) return true;
            var prior = candidates.FirstOrDefault(pair => pair.Value == native);
            var token = prior.Key ?? Guid.NewGuid().ToString("N");
            next[token] = native!;
            visible.Add(new(token, native!.Title, native.Application));
            return true;
        }, IntPtr.Zero);
        candidates.Clear();
        foreach (var item in next) candidates.Add(item.Key, item.Value);
        return visible.OrderBy(item => item.Application, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public async Task<WindowTextSnapshot> ReadAsync(string candidateToken, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var correlation = Convert.ToHexString(SHA256.HashData(Guid.NewGuid().ToByteArray()))[..16];
        Record(WindowTextReadStage.Requested);
        if (cancellationToken.IsCancellationRequested)
        {
            Record(WindowTextReadStage.Cancelled);
            cancellationToken.ThrowIfCancellationRequested();
        }
        if (!candidates.TryGetValue(candidateToken, out var selected) || !Matches(selected))
            return Complete(Empty(selected, WindowTextReadStatus.Unavailable));
        if (!WorkerGate.Wait(0)) return Complete(Empty(selected, WindowTextReadStatus.Busy));
        var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        operation.CancelAfter(WindowTextLimits.ReadDeadline);
        var completed = new TaskCompletionSource<WindowTextSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            try
            {
                Record(WindowTextReadStage.WorkerStarted);
                operation.Token.ThrowIfCancellationRequested();
                if (!Matches(selected)) { completed.TrySetResult(Empty(selected, WindowTextReadStatus.Unavailable)); return; }
                var contentRoot = WindowTextReadRoot.Select(selected.Handle, operation.Token);
                var root = AutomationElement.FromHandle(contentRoot);
                operation.Token.ThrowIfCancellationRequested();
                var scope = new WindowTextNativeScope(selected.Handle, selected.ProcessId, contentRoot);
                var result = WindowTextCollector.Collect(new AutomationWindowTextNode(root, scope), selected.ProcessId, operation.Token);
                operation.Token.ThrowIfCancellationRequested();
                completed.TrySetResult(Matches(selected)
                    && WindowTextReadRoot.Select(selected.Handle, operation.Token) == contentRoot
                    ? new(selected.Title, selected.Application, result.Text, DateTimeOffset.UtcNow, result.Status)
                    : Empty(selected, WindowTextReadStatus.Unavailable));
            }
            catch (OperationCanceledException) { completed.TrySetResult(Empty(selected, WindowTextReadStatus.TimedOut)); }
            catch (Exception exception) when (exception is ElementNotAvailableException or InvalidOperationException
                or COMException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            { completed.TrySetResult(Empty(selected, WindowTextReadStatus.Unavailable)); }
            finally { operation.Dispose(); WorkerGate.Release(); Record(WindowTextReadStage.WorkerReleased); }
        }) { IsBackground = true, Name = "FgoPet selected-window text" };
        worker.SetApartmentState(ApartmentState.MTA);
        try { worker.Start(); }
        catch { operation.Dispose(); WorkerGate.Release(); throw; }
        try
        {
            var result = await completed.Task.WaitAsync(WindowTextLimits.ReadDeadline, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return Complete(disposed ? Empty(selected, WindowTextReadStatus.Unavailable) : result);
        }
        catch (TimeoutException)
        {
            Record(WindowTextReadStage.Deadline, WindowTextReadStatus.TimedOut);
            return Complete(Empty(selected, WindowTextReadStatus.TimedOut));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { Record(WindowTextReadStage.Cancelled); throw; }
        // Cancellation suppresses any late content. The linked token is released by the worker,
        // so it remains valid even if a provider is still inside a synchronous call.

        WindowTextSnapshot Complete(WindowTextSnapshot result)
        {
            Record(WindowTextReadStage.Completed, result.Status, result.Text.Length);
            return result;
        }
        void Record(WindowTextReadStage stage, WindowTextReadStatus? status = null, int count = 0)
        {
            try { diagnostics?.Record(new(DateTimeOffset.UtcNow, correlation, stage, status, count)); }
            catch (Exception) { /* Best-effort diagnostics cannot change selection/read authority. */ }
        }
    }

    private static WindowTextSnapshot Empty(NativeCandidate? selected, WindowTextReadStatus status) =>
        new(selected?.Title ?? "Unavailable window", selected?.Application ?? "Unknown", "", DateTimeOffset.UtcNow, status);

    private static bool Matches(NativeCandidate selected) => TryDescribe(selected.Handle, out var current) && current == selected;

    private static bool TryDescribe(IntPtr handle, out NativeCandidate? candidate)
    {
        candidate = null;
        if (!NativeMethods.IsWindow(handle) || !NativeMethods.IsWindowVisible(handle)
            || NativeMethods.GetWindow(handle, 4) != IntPtr.Zero || handle == NativeMethods.GetShellWindow()) return false;
        NativeMethods.GetWindowThreadProcessId(handle, out var pid);
        if (pid == 0 || pid == Environment.ProcessId) return false;
        try
        {
            using var process = Process.GetProcessById(checked((int)pid));
            var name = process.ProcessName;
            if (ExcludedProcesses.Contains(name)) return false;
            var title = new StringBuilder(161);
            NativeMethods.GetWindowText(handle, title, title.Capacity);
            if (string.IsNullOrWhiteSpace(title.ToString())) return false;
            candidate = new(handle, process.Id, process.StartTime.ToUniversalTime().Ticks, title.ToString(),
                name.Length <= 160 ? name : name[..160]);
            return true;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException
            or ArgumentException or OverflowException or NotSupportedException) { return false; }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        candidates.Clear();
        lifetime.Cancel();
        lifetime.Dispose();
    }

    private sealed record NativeCandidate(IntPtr Handle, int ProcessId, long ProcessStartedUtc, string Title, string Application);

    private static class NativeMethods
    {
        internal delegate bool EnumWindowsCallback(IntPtr handle, IntPtr parameter);
        [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);
        [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr handle);
        [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr handle);
        [DllImport("user32.dll")] internal static extern IntPtr GetWindow(IntPtr handle, uint command);
        [DllImport("user32.dll")] internal static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(IntPtr handle, StringBuilder text, int maximum);
    }
}
