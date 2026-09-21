using System.Windows;
using System.IO;
using FgoPet.App.Bootstrap;
using FgoPet.App.Lifetime;
using FgoPet.Infrastructure.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Application = System.Windows.Application;

namespace FgoPet.App;

public partial class App : Application
{
    private ServiceProvider? _provider;
    private SingleInstanceCoordinator? _singleInstance;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var activation = e.Args.FirstOrDefault(path => path.EndsWith(".fgopetpack", StringComparison.OrdinalIgnoreCase))
            ?? "--activate";
        var instanceName = Environment.GetEnvironmentVariable("FGO_PET_PIPE_SUFFIX") ?? "main";
        if (!SingleInstanceCoordinator.TryCreatePrimary(instanceName, out _singleInstance, out var isPrimary) || !isPrimary)
        {
            SingleInstanceCoordinator.ForwardActivation(instanceName, activation, TimeSpan.FromSeconds(2));
            Shutdown(0);
            return;
        }

        try
        {
            _provider = ServiceRegistration.AddFgoPet(new ServiceCollection(), e.Args).BuildServiceProvider();
            _provider.GetRequiredService<Theming.ThemeService>().Initialize();
        }
        catch (Exception error)
        {
            ShowStartupError(error);
            return;
        }

        try
        {
            var startup = _provider.GetRequiredService<AppStartup>();
            var singleInstance = _singleInstance ?? throw new InvalidOperationException("主实例协调器未初始化。");
            singleInstance.ListenForActivation(payload =>
                Dispatcher.BeginInvoke(async () =>
                    await startup.StartAsync(payload == "--activate" ? [] : [payload])));
            await startup.StartAsync(e.Args);
        }
        catch (Exception error)
        {
            ShowStartupError(error);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        _provider?.Dispose();
        base.OnExit(e);
    }

    private void ShowStartupError(Exception error)
    {
        try
        {
            var stateRoot = Environment.GetEnvironmentVariable("FGO_PET_STATE_ROOT");
            if (!string.IsNullOrWhiteSpace(stateRoot))
            {
                Directory.CreateDirectory(stateRoot);
                // Exception.ToString() carries the full stack trace, whose frames embed source
                // absolute paths; redact the exception body only — stateRoot is an operator-configured
                // destination, not user data.
                File.WriteAllText(
                    Path.Combine(stateRoot, "startup-error.log"),
                    $"{DateTimeOffset.UtcNow:O}{Environment.NewLine}{ResourceDiagnostics.Redact($"{error}")}{Environment.NewLine}");
            }
        }
        catch
        {
            // Startup diagnostics must never replace the user-facing error path.
        }

        MessageBox.Show(
            error.Message,
            "FgoPet 启动失败",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        Shutdown(2);
    }
}
