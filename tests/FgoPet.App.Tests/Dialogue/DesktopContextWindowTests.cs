using System.Diagnostics;
using System.Windows.Interop;
using FgoPet.Plugin.DesktopContext;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

public sealed class DesktopContextWindowTests
{
    [Fact]
    public void Controlled_hidden_window_maps_to_own_process_without_reading_its_title_or_body()
    {
        StaTest.Run(() =>
        {
            // Only our own hidden test handle is queried; no desktop enumeration or model/network call.
            using var source = new HwndSource(new HwndSourceParameters("synthetic body must not be returned")
                { Width = 1, Height = 1, WindowStyle = unchecked((int)0x80000000) });
            var application = WindowsApplicationDiscovery.ReadWindow(source.Handle, source.Handle, out var unavailable);
            Assert.False(unavailable);
            Assert.NotNull(application);
            using var ownProcess = Process.GetCurrentProcess();
            Assert.Equal(ownProcess.ProcessName, application.Name);
            Assert.Equal(1, application.WindowCount);
            Assert.True(application.IsForeground); // Matches the supplied sampled handle, not an actual focus change.
        });
    }
}
