using System.IO;
using FgoPet.App.Lifetime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FgoPet.App.Bootstrap;

public static class ServiceRegistration
{
    public static IServiceCollection AddFgoPet(this IServiceCollection services, string[] args) => services
        .AddSingleton(TimeProvider.System)
        .AddLogging(builder => builder.AddDebug())
        .AddSingleton<TextWriter>(Console.Out)
        .AddSingleton<IAppLifetime, ApplicationLifetime>()
        .AddSingleton<AppStartup>();
}