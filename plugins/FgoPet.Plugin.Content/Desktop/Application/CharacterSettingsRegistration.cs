using FgoPet.App.Servants;
using FgoPet.App.Theming;
using FgoPet.Character.Settings;
using FgoPet.Core.Portraits;
using FgoPet.UiSdk;
using Microsoft.Extensions.DependencyInjection;

namespace FgoPet.App.Settings;

public static class CharacterSettingsRegistration
{
    public static IServiceCollection AddCharacterSettings(this IServiceCollection services)
    {
        services.AddSingleton<PersonalizationViewModel>();
        services.AddSingleton<ISettingsWebPage>(provider => new PersonalizationWebPage(
            provider.GetRequiredService<ICharacterSettingsStore>(),
            provider.GetService<IPortraitController>(),
            provider.GetRequiredService<ThemeService>(),
            provider.GetRequiredService<ServantLibraryViewModel>()));
        services.AddSingleton<ISettingsWebPage>(provider => new ThemeWebPage(
            provider.GetRequiredService<ThemeService>()));
        services.AddSingleton<ISettingsWebPage>(provider => new RolePackagesWebPage(
            provider.GetRequiredService<ICharacterSettingsStore>(),
            provider.GetRequiredService<ServantLibraryViewModel>()));
        services.AddSingleton<ISettingsPageViewFactory>(provider => new SettingsPageViewFactory(
            nameof(SettingsSection.Personalization), _ => throw new InvalidOperationException("SETTINGS_USE_WEB_ROOT"),
            "个性化", "调整应用的个性化偏好。", "开始使用", ["外观", "角色", "个性化"], 20));
        services.AddSingleton<ISettingsPageViewFactory>(provider => new SettingsPageViewFactory(
            nameof(SettingsSection.Theme), _ => throw new InvalidOperationException("SETTINGS_USE_WEB_ROOT"),
            "主题", "选择界面的视觉主题。", "管理", ["亮色", "暗色", "主题"], 220));
        services.AddSingleton<ISettingsPageViewFactory>(provider => new SettingsPageViewFactory(
            nameof(SettingsSection.RolePackages), _ => throw new InvalidOperationException("SETTINGS_USE_WEB_ROOT"),
            "角色包", "安装、浏览并管理角色包。", "能力", ["角色", "外观", "安装"], 110));
        return services;
    }
}
