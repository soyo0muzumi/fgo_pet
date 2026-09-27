using System.Diagnostics;

namespace FgoPet.CodexAdapter.AppServer;

/// <summary>Starts an interactive Codex process for an existing remote thread.</summary>
public interface ICodexVisibleSessionLauncher
{
    Process Launch(string threadId, string taskId, string? executable, string workingDirectory);
}

public sealed class CodexVisibleSessionLauncher : ICodexVisibleSessionLauncher
{
    public Process Launch(string threadId, string taskId, string? executable, string workingDirectory)
    {
        var process = Process.Start(CreateStartInfo(executable, threadId, taskId, workingDirectory));
        return process ?? throw new IOException("codex_resume_start_failed");
    }

    public static ProcessStartInfo CreateStartInfo(
        string? executable,
        string threadId,
        string taskId,
        string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(threadId)) throw new ArgumentException("Thread ID is required.", nameof(threadId));
        if (string.IsNullOrWhiteSpace(taskId)) throw new ArgumentException("Task ID is required.", nameof(taskId));
        if (string.IsNullOrWhiteSpace(workingDirectory)) throw new ArgumentException("Working directory is required.", nameof(workingDirectory));

        var info = new ProcessStartInfo(CodexTaskExecutor.ResolveExecutable(executable))
        {
            UseShellExecute = false,
            CreateNoWindow = false,
            WorkingDirectory = Path.GetFullPath(workingDirectory),
        };
        info.ArgumentList.Add("resume");
        info.ArgumentList.Add(threadId.Trim());
        info.Environment["FGO_PET_AGENT_TASK"] = taskId.Trim();
        return info;
    }
}
