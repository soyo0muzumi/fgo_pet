using System.IO;
using System.Runtime.CompilerServices;

namespace FgoPet.Windows.Tests;

internal static class WindowsTestState
{
    private static readonly string Root = Path.Combine(
        Path.GetTempPath(),
        "fgo-windows-tests-" + Guid.NewGuid().ToString("N"));

    [ModuleInitializer]
    internal static void Initialize()
    {
        Directory.CreateDirectory(Root);
        Environment.SetEnvironmentVariable("FGO_PET_STATE_ROOT", Root);
        Environment.SetEnvironmentVariable(
            "FGO_PET_PIPE_SUFFIX",
            "windows-tests-" + Guid.NewGuid().ToString("N"));
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Cleanup();
    }

    private static void Cleanup()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch
        {
            // Test cleanup must not mask the test result.
        }
    }
}