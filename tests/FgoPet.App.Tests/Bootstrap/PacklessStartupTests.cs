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
        var shellCreated = false;
        var startup = NewStartup(lifetime, output, () =>
        {
            shellCreated = true;
            return new FakeAppShell();
        });

        startup.Start(["--smoke-test"]);

        Assert.Equal(0, lifetime.ExitCode);
        var text = output.ToString();
        Assert.Contains("no-pack", text);
        Assert.Contains("portrait window NOT created", text);
        Assert.Contains("tray/servant-library startup confirmed", text);
        Assert.Contains("exiting 0", text);
        Assert.False(shellCreated);
    }

    [Fact]
    public async Task Packless_startup_starts_the_application_shell()
    {
        var lifetime = new FakeLifetime();
        var shell = new FakeAppShell();
        var startup = NewStartup(lifetime, new StringWriter(), shell);

        await startup.StartAsync([]);

        Assert.Null(lifetime.ExitCode);
        Assert.True(shell.Started);
        Assert.Empty(shell.Arguments);
    }

    private static AppStartup NewStartup(FakeLifetime lifetime, TextWriter output, IAppShell? shell = null) =>
        NewStartup(lifetime, output, () => shell ?? new FakeAppShell());

    private static AppStartup NewStartup(FakeLifetime lifetime, TextWriter output, Func<IAppShell> shellFactory)
    {
        var logger = NullLogger<AppStartup>.Instance;
        return new AppStartup(logger, lifetime, shellFactory, output);
    }

    private sealed class FakeAppShell : IAppShell
    {
        public bool Started { get; private set; }
        public IReadOnlyList<string> Arguments { get; private set; } = Array.Empty<string>();

        public Task StartAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            Started = true;
            Arguments = arguments;
            return Task.CompletedTask;
        }
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
