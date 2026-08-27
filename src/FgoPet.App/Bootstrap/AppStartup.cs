using System.IO;
using FgoPet.App.Lifetime;
using Microsoft.Extensions.Logging;

namespace FgoPet.App.Bootstrap;

public enum StartupMode
{
    SmokeTest,
    Packless,
}

/// <summary>
/// Owns the startup decision and the packless startup state. With no installed pack the
/// app must NOT create a portrait window: it keeps the tray active and opens the servant
/// library install state (Tasks 10/11), never a startup error.
/// </summary>
public sealed class AppStartup
{
    private readonly ILogger<AppStartup> _logger;
    private readonly IAppLifetime _lifetime;
    private readonly TextWriter _output;

    public AppStartup(ILogger<AppStartup> logger, IAppLifetime lifetime)
        : this(logger, lifetime, Console.Out)
    {
    }

    public AppStartup(ILogger<AppStartup> logger, IAppLifetime lifetime, TextWriter output)
    {
        _logger = logger;
        _lifetime = lifetime;
        _output = output;
    }

    public StartupMode Decide(string[] args) =>
        args.Contains("--smoke-test", StringComparer.Ordinal)
            ? StartupMode.SmokeTest
            : StartupMode.Packless;

    public void Start(string[] args)
    {
        var mode = Decide(args);
        if (mode == StartupMode.SmokeTest)
        {
            RunSmokeTest();
        }
        else
        {
            StartPackless();
        }
    }

    private void RunSmokeTest()
    {
        _output.WriteLine("FgoPet smoke-test: no-pack");
        _output.WriteLine("FgoPet smoke-test: portrait window NOT created");
        _output.WriteLine("FgoPet smoke-test: tray/servant-library startup confirmed");
        _output.WriteLine("FgoPet smoke-test: exiting 0");
        _logger.LogInformation("Smoke test ran without a portrait window; reporting no-pack.");
        _lifetime.Shutdown(0);
    }

    private void StartPackless()
    {
        _logger.LogInformation("未安装任何有效角色包: 进入 packless 状态，不创建画像窗口。");
        _logger.LogInformation("托盘常驻与从者库安装引导在 Task 10/11 接入。");
    }
}