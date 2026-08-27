using System.IO;
using FgoPet.App.Bootstrap;
using FgoPet.App.Lifetime;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FgoPet.App.Tests.Bootstrap;

public sealed class PacklessStartupTests
{
    [Fact]
    public void Decide_returns_SmokeTest_for_the_smoke_flag()
    {
        var startup = NewStartup(new FakeLifetime(), new StringWriter());
        Assert.Equal(StartupMode.SmokeTest, startup.Decide(["--smoke-test"]));
    }

    [Fact]
    public void Decide_returns_Packless_by_default()
    {
        var startup = NewStartup(new FakeLifetime(), new StringWriter());
        Assert.Equal(StartupMode.Packless, startup.Decide([]));
    }

    [Fact]
    public void Smoke_test_reports_no_pack_and_exits_zero()
    {
        var lifetime = new FakeLifetime();
        var output = new StringWriter();
        var startup = NewStartup(lifetime, output);

        startup.Start(["--smoke-test"]);

        Assert.Equal(0, lifetime.ExitCode);
        var text = output.ToString();
        Assert.Contains("no-pack", text);
        Assert.Contains("portrait window NOT created", text);
        Assert.Contains("tray/servant-library startup confirmed", text);
        Assert.Contains("exiting 0", text);
    }

    [Fact]
    public void Packless_startup_does_not_shutdown_or_report_errors()
    {
        var lifetime = new FakeLifetime();
        var startup = NewStartup(lifetime, new StringWriter());

        startup.Start([]);

        Assert.Null(lifetime.ExitCode);
    }

    private static AppStartup NewStartup(FakeLifetime lifetime, TextWriter output)
    {
        var logger = NullLogger<AppStartup>.Instance;
        return new AppStartup(logger, lifetime, output);
    }

    private sealed class FakeLifetime : IAppLifetime
    {
        public int? ExitCode { get; private set; }

        public bool IsPetVisible { get; } = false;

        public void Shutdown(int exitCode) => ExitCode = exitCode;

        public void RequestNormalExit() => ExitCode = 0;

        public void ShowOrHidePet()
        {
        }

        public void AttachPetWindow(System.Windows.Window window)
        {
        }
    }
}