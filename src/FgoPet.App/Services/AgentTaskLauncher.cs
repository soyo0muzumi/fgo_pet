using System.Diagnostics;
using System.IO;

namespace FgoPet.App.Services;

/// <summary>Launches a visible session for a remote Agent task.</summary>
public interface IAgentTaskLauncher
{
    Task LaunchAsync(string threadId, string taskId, CancellationToken cancellationToken = default);
}

public sealed class CodexTaskLauncher : IAgentTaskLauncher
{
    public Task LaunchAsync(string threadId, string taskId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var info = CreateStartInfo(threadId, taskId);
        if (Process.Start(info) is null) throw new IOException("codex_resume_start_failed");
        return Task.CompletedTask;
    }

    internal static ProcessStartInfo CreateStartInfo(string threadId, string taskId)
    {
        if (string.IsNullOrWhiteSpace(threadId)) throw new ArgumentException("Thread ID is required.", nameof(threadId));
        if (string.IsNullOrWhiteSpace(taskId)) throw new ArgumentException("Task ID is required.", nameof(taskId));

        var executable = Environment.GetEnvironmentVariable("FGO_PET_CODEX_EXE");
        if (string.IsNullOrWhiteSpace(executable))
        {
            executable = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(path => Path.Combine(path.Trim('"'), "codex.exe"))
                .FirstOrDefault(File.Exists);
        }

        if (string.IsNullOrWhiteSpace(executable) || !Path.IsPathFullyQualified(executable) || !File.Exists(executable)
            || !string.Equals(Path.GetFileName(executable), "codex.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("codex_not_installed");
        }

        var info = new ProcessStartInfo(Path.GetFullPath(executable))
        {
            UseShellExecute = false,
            CreateNoWindow = false,
            WorkingDirectory = Environment.CurrentDirectory,
        };
        info.ArgumentList.Add("resume");
        info.ArgumentList.Add(threadId.Trim());
        info.Environment["FGO_PET_AGENT_TASK"] = taskId.Trim();
        return info;
    }
}
