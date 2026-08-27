using System.Windows;
using FgoPet.App.Bootstrap;
using Microsoft.Extensions.DependencyInjection;
using Application = System.Windows.Application;

namespace FgoPet.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ServiceProvider provider;
        try
        {
            provider = ServiceRegistration.AddFgoPet(new ServiceCollection(), e.Args).BuildServiceProvider();
        }
        catch (Exception error)
        {
            ShowStartupError(error);
            return;
        }

        try
        {
            provider.GetRequiredService<AppStartup>().Start(e.Args);
        }
        catch (Exception error)
        {
            ShowStartupError(error);
        }
    }

    private void ShowStartupError(Exception error)
    {
        MessageBox.Show(
            error.Message,
            "FgoPet 启动失败",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        Shutdown(2);
    }
}