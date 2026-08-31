using System.Diagnostics;
using FgoPet.AgentProtocol.Messages;

namespace FgoPet.CodexAdapter.AppServer;

public interface ICodexTaskExecutor
{
    Task<string> ExecuteAsync(DispatchTaskRequest request, Func<string, string?, Task> report, CancellationToken cancellationToken);
}

/// <summary>Each confirmed dispatch owns a child app-server; shutdown/revocation kills only that child tree.</summary>
public sealed class CodexTaskExecutor(ICodexTargetResolver targets, string? executable = null) : ICodexTaskExecutor
{
    public async Task<string> ExecuteAsync(DispatchTaskRequest request, Func<string, string?, Task> report, CancellationToken cancellationToken)
    {
        var directory = targets.Resolve(request.TargetId); // Validate before launching anything.
        var info = new ProcessStartInfo(ResolveExecutable(executable))
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        info.ArgumentList.Add("app-server");
        info.ArgumentList.Add("--stdio");
        info.Environment["FGO_PET_EXECUTOR_CHILD"] = "1";
        using var process = Process.Start(info) ?? throw new IOException("codex_start_failed");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lifetime.CancelAfter(TimeSpan.FromMinutes(30));
        var diagnostics = DrainDiagnosticsAsync(process.StandardError, lifetime.Token);
        await using var rpc = new JsonRpcLineClient(process.StandardOutput.BaseStream, process.StandardInput.BaseStream);
        try
        {
            await rpc.InitializeAsync(lifetime.Token).ConfigureAwait(false);
            var task = await new CodexAppServerClient(rpc, targets).StartTaskAsync(request, lifetime.Token).ConfigureAwait(false);
            await report("task_started", task.TaskId).ConfigureAwait(false);
            var progressSent = false;
            while (true)
            {
                var notification = await rpc.ReadNotificationAsync(lifetime.Token).ConfigureAwait(false);
                var method = notification.GetProperty("method").GetString();
                if (method == "fgo/approvalDenied")
                {
                    await report("attention_required", task.TaskId).ConfigureAwait(false);
                    return "task_cancelled";
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
        throw new IOException("codex_not_installed");
    }

    private static async Task DrainDiagnosticsAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[2048];
        while (await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false) != 0) { }
    }
}
