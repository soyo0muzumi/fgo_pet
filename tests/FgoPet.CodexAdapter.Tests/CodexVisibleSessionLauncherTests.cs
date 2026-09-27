using FgoPet.CodexAdapter.AppServer;
using Xunit;

namespace FgoPet.CodexAdapter.Tests;

public sealed class CodexVisibleSessionLauncherTests
{
    [Fact]
    public void Resume_start_info_targets_the_existing_thread_and_preserves_task_context()
    {
        var root = Path.Combine(Path.GetTempPath(), "fgo-visible-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var executable = Path.Combine(root, "codex.exe");
        File.WriteAllText(executable, string.Empty);
        try
        {
            var info = CodexVisibleSessionLauncher.CreateStartInfo(
                executable, "thread-123", "dispatch-456", root);

            Assert.Equal(executable, info.FileName);
            Assert.Equal(new[] { "resume", "thread-123" }, info.ArgumentList);
            Assert.Equal("dispatch-456", info.Environment["FGO_PET_AGENT_TASK"]);
            Assert.Equal(root, info.WorkingDirectory);
            Assert.False(info.UseShellExecute);
            Assert.False(info.CreateNoWindow);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
