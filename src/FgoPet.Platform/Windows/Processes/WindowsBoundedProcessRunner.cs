using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using FgoPet.Platform.Processes;
using Microsoft.Win32.SafeHandles;

namespace FgoPet.Platform.Windows.Processes;

/// <summary>Suspended launch, explicit inherited handles, bounded capture and a kill-on-close job.</summary>
public sealed class WindowsBoundedProcessRunner(IBoundedProcessDiagnostics? diagnostics = null) : IBoundedProcessRunner
{
    private readonly object diagnosticGate = new();
    private readonly Queue<(BoundedProcessNotification Notification, TaskCompletionSource Done)> diagnosticQueue = new();
    private bool diagnosticWorker;
    public async ValueTask<BoundedProcessResult> RunAsync(BoundedProcessRequest request, CancellationToken token)
    {
        Validate(request);
        request = request with { Correlation = request.Correlation ?? Guid.NewGuid().ToString("N")[..24].ToUpperInvariant() };
        _ = Notify(request, BoundedProcessStage.InputReceived);
        _ = Notify(request, BoundedProcessStage.Queued);
        var result = await ExecuteAsync(request, token);
        var recorded = Notify(request, result.Exit switch {
            BoundedProcessExit.Completed => BoundedProcessStage.Completed,
            BoundedProcessExit.TimedOut => BoundedProcessStage.TimedOut,
            BoundedProcessExit.Cancelled => BoundedProcessStage.Cancelled,
            _ => BoundedProcessStage.Failed }, result.ExitCode, result.ErrorCode);
        try { await recorded.WaitAsync(TimeSpan.FromMilliseconds(100)); }
        catch (TimeoutException) { /* A diagnostic sink cannot delay process supervision or completion. */ }
        return result;
    }

    private async ValueTask<BoundedProcessResult> ExecuteAsync(BoundedProcessRequest request, CancellationToken token)
    {
        if (token.IsCancellationRequested) return Result(false, BoundedProcessExit.Cancelled, "PROCESS_CANCELLED");
        var resumed = false;
        var assigned = false;
        SafeFileHandle? process = null;
        using var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid) return Result(false, BoundedProcessExit.LaunchFailed, "PROCESS_JOB_UNAVAILABLE");
        var limits = new JobLimits { Basic = new() { Flags = 0x2000 | 0x200 | 0x8, ActiveProcesses = (uint)request.MaxProcesses },
            JobMemory = (UIntPtr)(ulong)request.MaxMemoryBytes };
        if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<JobLimits>()))
            return Result(false, BoundedProcessExit.LaunchFailed, "PROCESS_JOB_UNAVAILABLE");
        var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Inherit = 1 };
        SafeFileHandle? outputRead = null, outputWrite = null, errorRead = null, errorWrite = null, input = null, inputWrite = null;
        IntPtr attributes = IntPtr.Zero, handles = IntPtr.Zero;
        var initialized = false;
        Task<Capture>? stdout = null, stderr = null;
        Task? stdin = null;
        try
        {
            if (!CreatePipe(out outputRead, out outputWrite, ref security, 0) ||
                !CreatePipe(out errorRead, out errorWrite, ref security, 0) ||
                !SetHandleInformation(outputRead, 1, 0) || !SetHandleInformation(errorRead, 1, 0))
                return Result(false, BoundedProcessExit.LaunchFailed, "PROCESS_PIPE_UNAVAILABLE");
            if (!CreatePipe(out input, out inputWrite, ref security, 0) || !SetHandleInformation(inputWrite, 1, 0))
                return Result(false, BoundedProcessExit.LaunchFailed, "PROCESS_INPUT_UNAVAILABLE");
            nuint size = 0;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal(checked((int)size));
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size))
                return Result(false, BoundedProcessExit.LaunchFailed, "PROCESS_ATTRIBUTES_UNAVAILABLE");
            initialized = true;
            handles = Marshal.AllocHGlobal(3 * IntPtr.Size);
            Marshal.WriteIntPtr(handles, 0, input.DangerousGetHandle());
            Marshal.WriteIntPtr(handles, IntPtr.Size, outputWrite.DangerousGetHandle());
            Marshal.WriteIntPtr(handles, 2 * IntPtr.Size, errorWrite.DangerousGetHandle());
            if (!UpdateProcThreadAttribute(attributes, 0, (nuint)0x20002, handles, (nuint)(3 * IntPtr.Size), IntPtr.Zero, IntPtr.Zero))
                return Result(false, BoundedProcessExit.LaunchFailed, "PROCESS_ATTRIBUTES_UNAVAILABLE");
            var start = new StartupInfoEx { Startup = new() { Size = Marshal.SizeOf<StartupInfoEx>(), Flags = 0x100,
                Input = input.DangerousGetHandle(), Output = outputWrite.DangerousGetHandle(), Error = errorWrite.DangerousGetHandle() }, Attributes = attributes };
            var commandLine = new StringBuilder(Quote(request.Executable) + " " + string.Join(" ", request.Arguments.Select(Quote)));
            token.ThrowIfCancellationRequested();
            request.BeforeStart?.Invoke();
            _ = Notify(request, BoundedProcessStage.Launching);
            if (!CreateProcessW(request.Executable, commandLine, IntPtr.Zero, IntPtr.Zero, true,
                0x4 | 0x08000000 | 0x00080000, IntPtr.Zero, request.WorkingDirectory, ref start, out var info))
                return Result(false, BoundedProcessExit.LaunchFailed, "PROCESS_LAUNCH_FAILED");
            process = new(info.Process, ownsHandle: true);
            using var thread = new SafeFileHandle(info.Thread, ownsHandle: true);
            if (!AssignProcessToJobObject(job, process))
            {
                var stopped = StopUnassignedProcess(process);
                return Result(false, stopped ? BoundedProcessExit.LaunchFailed : BoundedProcessExit.Unconfirmed,
                    stopped ? "PROCESS_JOB_ASSIGN_FAILED" : "PROCESS_TERMINATION_UNCONFIRMED");
            }
            assigned = true;
            outputWrite.Dispose(); outputWrite = null;
            errorWrite.Dispose(); errorWrite = null;
            input.Dispose(); input = null;
            stdout = CaptureAsync(outputRead, request.MaxOutputBytes); outputRead = null;
            stderr = CaptureAsync(errorRead, request.MaxOutputBytes); errorRead = null;
            stdin = SendInputAsync(inputWrite, request.StandardInput); inputWrite = null;
            token.ThrowIfCancellationRequested();
            request.BeforeStart?.Invoke();
            if (ResumeThread(thread) == uint.MaxValue)
                return Result(false, BoundedProcessExit.LaunchFailed, "PROCESS_RESUME_FAILED");
            resumed = true;
            _ = Notify(request, BoundedProcessStage.Started);
            var elapsed = Stopwatch.StartNew();
            var exit = BoundedProcessExit.Completed;
            while (true)
            {
                var wait = WaitForSingleObject(process, 0);
                if (wait == 0) break;
                if (wait != 258) { exit = BoundedProcessExit.Unconfirmed; break; }
                if (token.IsCancellationRequested) { exit = BoundedProcessExit.Cancelled; break; }
                if (elapsed.Elapsed >= request.Timeout) { exit = BoundedProcessExit.TimedOut; break; }
                await Task.Delay(20, CancellationToken.None);
            }
            int? exitCode = null;
            if (exit == BoundedProcessExit.Completed)
            {
                if (GetExitCodeProcess(process, out var code)) exitCode = unchecked((int)code);
                else exit = BoundedProcessExit.Unconfirmed;
            }
            // Stop inherited descendants even when the root exited; no breakaway flags are enabled.
            var terminated = TerminateJobObject(job, 1);
            if (!terminated || !await WaitForJobEmpty(job, TimeSpan.FromSeconds(5))) exit = BoundedProcessExit.Unconfirmed;
            job.Dispose();
            try
            {
                var captures = await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5));
                await stdin.WaitAsync(TimeSpan.FromSeconds(5));
                return new(true, exit, exit == BoundedProcessExit.Completed ? exitCode : null,
                    captures[0].Text, captures[1].Text, captures.Any(item => item.Truncated),
                    exit == BoundedProcessExit.Completed ? null : "PROCESS_" + exit.ToString().ToUpperInvariant());
            }
            catch (Exception error) when (error is TimeoutException or IOException) { return Result(true, BoundedProcessExit.Unconfirmed, "PROCESS_OUTPUT_UNCONFIRMED"); }
        }
        catch (OperationCanceledException) { return Result(resumed, BoundedProcessExit.Cancelled, "PROCESS_CANCELLED"); }
        catch (Exception)
        { return Result(resumed, resumed ? BoundedProcessExit.Unconfirmed : BoundedProcessExit.LaunchFailed, "PROCESS_FAILED"); }
        finally
        {
            if (process is not null)
            {
                if (!assigned) StopUnassignedProcess(process);
                else if (!job.IsClosed) TerminateJobObject(job, 1);
                process.Dispose();
            }
            job.Dispose();
            outputRead?.Dispose(); outputWrite?.Dispose(); errorRead?.Dispose(); errorWrite?.Dispose(); input?.Dispose(); inputWrite?.Dispose();
            if (initialized) DeleteProcThreadAttributeList(attributes);
            if (attributes != IntPtr.Zero) Marshal.FreeHGlobal(attributes);
            if (handles != IntPtr.Zero) Marshal.FreeHGlobal(handles);
            Observe(stdout); Observe(stderr); Observe(stdin);
        }
    }

    private static async Task<bool> WaitForJobEmpty(SafeFileHandle job, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        do
        {
            if (!QueryInformationJobObject(job, 1, out var accounting, (uint)Marshal.SizeOf<JobAccounting>(), IntPtr.Zero)) return false;
            if (accounting.ActiveProcesses == 0) return true;
            await Task.Delay(20);
        } while (watch.Elapsed < timeout);
        return false;
    }
    private sealed record Capture(string Text, bool Truncated);
    private static async Task SendInputAsync(SafeFileHandle handle, ReadOnlyMemory<byte> input)
    {
        using var stream = new FileStream(handle, FileAccess.Write, 4096, isAsync: false);
        await stream.WriteAsync(input);
        await stream.FlushAsync();
    }
    private static async Task<Capture> CaptureAsync(SafeFileHandle handle, int limit)
    {
        using var stream = new FileStream(handle, FileAccess.Read, 4096, isAsync: false);
        using var retained = new MemoryStream();
        var buffer = new byte[4096];
        var truncated = false;
        while (true)
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0) break;
            var keep = Math.Min(read, limit - (int)retained.Length);
            if (keep > 0) retained.Write(buffer, 0, keep);
            if (keep < read) truncated = true;
        }
        return new(Encoding.UTF8.GetString(retained.ToArray()), truncated);
    }
    private static void Observe(Task? task)
    {
        if (task is not null) _ = task.ContinueWith(done => { _ = done.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
    private static BoundedProcessResult Result(bool started, BoundedProcessExit exit, string code) => new(started, exit, null, "", "", false, code);
    private static bool StopUnassignedProcess(SafeFileHandle process)
    {
        if (WaitForSingleObject(process, 0) == 0) return true;
        TerminateProcess(process, 1);
        return WaitForSingleObject(process, 5000) == 0;
    }
    private Task Notify(BoundedProcessRequest request, BoundedProcessStage stage, int? exit = null, string? code = null)
    {
        if (diagnostics is null) return Task.CompletedTask;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (diagnosticGate)
        {
            // One worker and a bounded queue per runner, even when the trusted diagnostic sink blocks.
            if (diagnosticQueue.Count >= 64) return Task.CompletedTask;
            diagnosticQueue.Enqueue((new(DateTimeOffset.UtcNow, request.Correlation!, stage, exit, code), done));
            if (!diagnosticWorker)
            {
                diagnosticWorker = true;
                _ = Task.Run(DrainDiagnostics);
            }
        }
        return done.Task;
    }
    private void DrainDiagnostics()
    {
        while (true)
        {
            (BoundedProcessNotification Notification, TaskCompletionSource Done) item;
            lock (diagnosticGate)
            {
                if (!diagnosticQueue.TryDequeue(out item)) { diagnosticWorker = false; return; }
            }
            try { diagnostics!.Record(item.Notification); }
            catch (Exception) { /* Diagnostic errors do not change execution evidence. */ }
            finally { item.Done.TrySetResult(); }
        }
    }
    private static void Validate(BoundedProcessRequest r)
    {
        if (r is null || !Path.IsPathFullyQualified(r.Executable) || r.Executable.Length > 1024 ||
            r.Executable.Contains('\0') || !Path.IsPathFullyQualified(r.WorkingDirectory) || !Directory.Exists(r.WorkingDirectory) ||
            r.Arguments.IsDefault || r.Arguments.Length > 64 || r.Arguments.Any(arg => arg is null || arg.Contains('\0')) ||
            r.Arguments.Sum(arg => (long)arg.Length) + r.Executable.Length > 24000 ||
            r.Timeout < TimeSpan.FromMilliseconds(50) || r.Timeout > TimeSpan.FromMinutes(2) ||
            r.MaxOutputBytes is < 1 or > 4194304 || r.MaxMemoryBytes is < 16777216 or > 1073741824 || r.MaxProcesses is < 1 or > 32 ||
            r.StandardInput.Length > 8388608)
            throw new ArgumentException("Invalid bounded process request.");
        if (r.Correlation is not null && (r.Correlation.Length != 24 ||
            !r.Correlation.All(c => c is >= 'A' and <= 'F' or >= '0' and <= '9')))
            throw new ArgumentException("Invalid process correlation.");
    }
    private static string Quote(string value)
    {
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') result.Append('\\', slashes * 2 + 1).Append(c);
            else result.Append('\\', slashes).Append(c);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Length; public IntPtr Descriptor; public int Inherit; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo
    {
        public int Size; public IntPtr Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XChars, YChars, Fill, Flags;
        public ushort Show, ReservedLength; public IntPtr ReservedBytes, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo Startup; public IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)] private struct JobBasic
    {
        public long ProcessTime, JobTime; public uint Flags; public UIntPtr MinimumSet, MaximumSet;
        public uint ActiveProcesses; public UIntPtr Affinity; public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)] private struct JobLimits
    {
        public JobBasic Basic; public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [StructLayout(LayoutKind.Sequential)] private struct JobAccounting
    {
        public long UserTime, KernelTime, PeriodUserTime, PeriodKernelTime; public uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern SafeFileHandle CreateJobObjectW(IntPtr security, string? name);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref JobLimits limits, uint size);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryInformationJobObject(SafeFileHandle job, int infoClass, out JobAccounting information, uint size, IntPtr returned);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes attributes, uint size);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, ref SecurityAttributes security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref nuint size);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, nuint attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateProcessW(string application, StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfoEx startup, out ProcessInformation information);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeFileHandle process);
    [DllImport("kernel32.dll")] private static extern uint ResumeThread(SafeFileHandle thread);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(SafeFileHandle handle, uint milliseconds);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetExitCodeProcess(SafeFileHandle process, out uint exit);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateProcess(SafeFileHandle process, uint exit);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateJobObject(SafeFileHandle job, uint exit);
}
