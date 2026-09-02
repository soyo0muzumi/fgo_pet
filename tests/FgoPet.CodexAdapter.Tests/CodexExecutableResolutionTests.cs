using FgoPet.CodexAdapter.AppServer;
using Xunit;

namespace FgoPet.CodexAdapter.Tests;

public sealed class CodexExecutableResolutionTests
{
    [Fact]
    public void Finds_codex_in_versioned_local_openai_install()
    {
        var root = Path.Combine(Path.GetTempPath(), "fgo-codex-install-" + Guid.NewGuid().ToString("N"));
        var versionedBin = Path.Combine(root, "b99306303521e97e");
        var expected = Path.Combine(versionedBin, "codex.exe");
        try
        {
            Directory.CreateDirectory(versionedBin);
            File.WriteAllText(expected, "test");

            Assert.Equal(expected, CodexExecutableLocator.FindInCodexBin(root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
