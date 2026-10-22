using FgoPet.UiSdk;
using Microsoft.Extensions.DependencyInjection;

namespace FgoPet.Plugin.Qq.Desktop;

public static class QqSettingsRegistration
{
    /// <summary>The host owns the channel instance and its application lifetime.</summary>
    public static IServiceCollection AddQqSettings(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services
            .AddSingleton<ISettingsPageViewFactory>(_ => new SettingsPageViewFactory("Qq",
                _ => throw new InvalidOperationException("SETTINGS_USE_WEB_ROOT"),
                "QQ", "Continue a conversation in a private QQ chat.", "开始使用", ["QQ", "chat", "private"], 125))
            .AddSingleton<ISettingsWebPage>(provider => new QqSettingsWebPage(provider.GetRequiredService<QqChatPlugin>()));
    }
}
