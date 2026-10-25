using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace FgoPet.Testing;

internal static class TestStateIsolation
{
    // Test-only process setup. Ordinary dotnet test must never fall back to the installed user's state.
    [ModuleInitializer]
    internal static void Initialize()
    {
        var parent=Environment.GetEnvironmentVariable("FGO_PET_STATE_ROOT");
        if(string.IsNullOrWhiteSpace(parent))parent=Path.Combine(Path.GetTempPath(),"fgo-isolated-tests");
        var isolated=Path.Combine(Path.GetFullPath(parent),"host-"+Environment.ProcessId+"-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(isolated);
        Environment.SetEnvironmentVariable("FGO_PET_STATE_ROOT",isolated);
    }
}
