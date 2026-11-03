using FgoPet.Plugin.Browser;
using FgoPet.Plugin.WebSearch;
using FgoPet.UiSdk;
using Microsoft.Extensions.DependencyInjection;

namespace FgoPet.App.Settings;

internal static class WebAccessSettingsRegistration
{
    internal static IServiceCollection AddWebAccessSettings(this IServiceCollection services) => services
        .AddSingleton<ISettingsPageViewFactory>(_ => new SettingsPageViewFactory("WebAccess",
            _ => throw new InvalidOperationException("SETTINGS_USE_WEB_ROOT"),
            "Web access", "Web search and Chrome connection.", "开始使用", ["web", "search", "browser", "Chrome"], 130))
        .AddSingleton<ISettingsWebPage>(provider => new WebAccessSettingsWebPage(
            provider.GetRequiredService<WebSearchSettingsStore>(),
            provider.GetRequiredService<ChromeBrowserSettingsStore>(),
            provider.GetRequiredService<WebSearchPlugin>().CheckConnectionAsync,
            provider.GetRequiredService<ChromeBrowserPlugin>().CheckConnectionAsync,
            provider.GetRequiredService<ChromeBrowserPlugin>().ReconnectAsync,
            AppContext.BaseDirectory));
}
