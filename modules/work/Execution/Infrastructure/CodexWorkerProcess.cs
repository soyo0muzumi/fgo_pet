using System.Diagnostics;
using FgoPet.AgentRuntime;

namespace FgoPet.Infrastructure.Agents;

/// <summary>Starts only the shipped companion executable, never an arbitrary command from an Agent message.</summary>
public sealed class CodexWorkerProcess(RelayRuntimeOptions options) : IDisposable
{
    private Process? _process;
    private readonly object _gate = new();
    private bool _disposed;

    public void EnsureStarted()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_process is { HasExited: false }) return;
            _process?.Dispose();
            var path = Path.Combine(Path.GetDirectoryName(options.RelayExecutablePath)!, "FgoPet.CodexAdapter.exe");
            if (!File.Exists(path)) throw new IOException("adapter_not_installed");
            var info = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true };
            info.ArgumentList.Add("worker");
            info.Environment["FGO_PET_STATE_ROOT"] = options.StateRoot;
            info.Environment["FGO_PET_PIPE_SUFFIX"] = options.PipeSuffix;
            _process = Process.Start(info) ?? throw new IOException("adapter_start_failed");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            if (_process is null) return;
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            finally { _process.Dispose(); _process = null; }
        }
    }
}
