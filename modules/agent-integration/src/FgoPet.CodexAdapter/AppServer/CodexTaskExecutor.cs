using System.Diagnostics;
using FgoPet.AgentProtocol.Messages;

namespace FgoPet.CodexAdapter.AppServer;

public interface ICodexTaskExecutor
{
    Task<string> ExecuteAsync(DispatchTaskRequest request, Func<string, string?, Task> report, CancellationToken cancellationToken);
}

/// <summary>Each confirmed dispatch owns a child app-server; shutdown/revocation kills only that child tree.</summary>
public sealed class CodexTaskExecutor : ICodexTaskExecutor
{
    private readonly ICodexTargetResolver _targets;
    private readonly string? _executable;
    private readonly ICodexVisibleSessionLauncher _visibleSessionLauncher;
    private readonly ICodexWorkerDiagnostics _diagnostics;

    public CodexTaskExecutor(
        ICodexTargetResolver targets,
        string? executable = null,
        ICodexVisibleSessionLauncher? visibleSessionLauncher = null,
        ICodexWorkerDiagnostics? diagnostics = null)
    {
        _targets = targets ?? throw new ArgumentNullException(nameof(targets));
        _executable = executable;
        _visibleSessionLauncher = visibleSessionLauncher ?? new CodexVisibleSessionLauncher();
        _diagnostics = diagnostics ?? NullCodexWorkerDiagnostics.Instance;
    }

    public async Task<string> ExecuteAsync(DispatchTaskRequest request, Func<string, string?, Task> report, CancellationToken cancellationToken)
    {
        string directory;
        try
        {
            directory = _targets.Resolve(request.TargetId); // Validate before launching anything.
            _diagnostics.Record("target.resolve", "ok", dispatchRequestId: request.DispatchRequestId);
        }
        catch (Exception error)
        {
            _diagnostics.Record("target.resolve", "failed", CodexWorkerDiagnostics.ErrorCode(error), request.DispatchRequestId);
            throw;
        }

        string executable;
        try
        {
            executable = ResolveExecutable(_executable);
            _diagnostics.Record("codex.resolve", "ok", dispatchRequestId: request.DispatchRequestId);
        }
        catch (Exception error)
        {
            _diagnostics.Record("codex.resolve", "failed", CodexWorkerDiagnostics.ErrorCode(error), request.DispatchRequestId);
            throw;
        }

        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        info.ArgumentList.Add("app-server");
        info.ArgumentList.Add("--stdio");
        info.Environment["FGO_PET_EXECUTOR_CHILD"] = "1";
        Process process;
        try
        {
            process = Process.Start(info) ?? throw new IOException("codex_start_failed");
            _diagnostics.Record("process.start", "ok", dispatchRequestId: request.DispatchRequestId);
        }
        catch (Exception error)
        {
            _diagnostics.Record("process.start", "failed", CodexWorkerDiagnostics.ErrorCode(error), request.DispatchRequestId);
            throw;
        }
        using (process)
        {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lifetime.CancelAfter(TimeSpan.FromMinutes(30));
        var diagnostics = DrainDiagnosticsAsync(process.StandardError, lifetime.Token);
        await using var rpc = new JsonRpcLineClient(process.StandardOutput.BaseStream, process.StandardInput.BaseStream);
        try
        {
            try
            {
                await rpc.InitializeAsync(lifetime.Token).ConfigureAwait(false);
                _diagnostics.Record("rpc.initialize", "ok", dispatchRequestId: request.DispatchRequestId);
            }
            catch (Exception error)
            {
                _diagnostics.Record("rpc.initialize", "failed", CodexWorkerDiagnostics.ErrorCode(error), request.DispatchRequestId);
                throw;
            }

            CodexStartedTask task;
            try
            {
                task = await new CodexAppServerClient(rpc, _targets).StartTaskAsync(request, lifetime.Token).ConfigureAwait(false);
                _diagnostics.Record("rpc.task_start", "ok", dispatchRequestId: request.DispatchRequestId);
            }
            catch (Exception error)
            {
                _diagnostics.Record("rpc.task_start", "failed", CodexWorkerDiagnostics.ErrorCode(error), request.DispatchRequestId);
                throw;
            }
            await report("task_started", task.TaskId).ConfigureAwait(false);
            var progressSent = false;
            while (true)
            {
                var notification = await rpc.ReadNotificationAsync(lifetime.Token).ConfigureAwait(false);
                var method = notification.GetProperty("method").GetString();
                if (method == "fgo/approvalRequired")
                {
                    await report("attention_required", task.TaskId).ConfigureAwait(false);
                    try
                    {
                        _visibleSessionLauncher.Launch(task.TaskId, request.DispatchRequestId, _executable, directory);
                        _diagnostics.Record("visible.launch", "ok", dispatchRequestId: request.DispatchRequestId);
                    }
                    catch (Exception error)
                    {
                        _diagnostics.Record("visible.launch", "failed", CodexWorkerDiagnostics.ErrorCode(error), request.DispatchRequestId);
                        throw;
                    }
                    return "awaiting_acceptance";
                }
                if (method == "fgo/approvalDenied")
                {
                    await report("attention_required", task.TaskId).ConfigureAwait(false);
                    return "task_cancelled";
                }
                if (method == "error")
                {
                    _diagnostics.Record("rpc.notification", "failed", "codex_error", request.DispatchRequestId);
                }
                if (!notification.TryGetProperty("params", out var parameters)) continue;
                if (parameters.TryGetProperty("threadId", out var thread) && thread.GetString() != task.TaskId) continue;
                if (method == "item/started" && !progressSent)
                {
                    progressSent = true;
                    await report("task_updated", task.TaskId).ConfigureAwait(false);
                }
                if (method != "turn/completed") continue;
                var turn = parameters.GetProperty("turn");
                if (turn.GetProperty("id").GetString() != task.TurnId) continue;
                return turn.GetProperty("status").GetString() switch
                {
                    "completed" => "task_completed", "interrupted" => "task_cancelled", _ => "task_failed",
                };
            }
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await lifetime.CancelAsync().ConfigureAwait(false);
            try { await diagnostics.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        }
    }

    public static string ResolveExecutable(string? configured = null)
    {
        configured ??= Environment.GetEnvironmentVariable("FGO_PET_CODEX_EXE");
        if (configured is not null)
        {
            if (!Path.IsPathFullyQualified(configured) || !File.Exists(configured)
                || !string.Equals(Path.GetFileName(configured), "codex.exe", StringComparison.OrdinalIgnoreCase))
                throw new IOException("codex_executable_invalid");
            return Path.GetFullPath(configured);
        }
        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var path = entry.Trim('"');
            if (!Path.IsPathFullyQualified(path)) continue;
            var candidate = Path.Combine(path, "codex.exe");
            if (File.Exists(candidate)) return candidate;
        }
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var installed = CodexExecutableLocator.FindInCodexBin(
            Path.Combine(localAppData, "OpenAI", "Codex", "bin"));
        if (installed is not null) return installed;
        throw new IOException("codex_not_installed");
    }

    private static async Task DrainDiagnosticsAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[2048];
        while (await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false) != 0) { }
    }
}
