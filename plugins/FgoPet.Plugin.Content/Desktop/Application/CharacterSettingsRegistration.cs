using FgoPet.App.Servants;
using FgoPet.Character.Settings;
using FgoPet.UiSdk;
using Microsoft.Extensions.DependencyInjection;

namespace FgoPet.App.Settings;

public static class CharacterSettingsRegistration
{
    public static IServiceCollection AddCharacterSettings(this IServiceCollection services)
    {
        services.AddSingleton<PersonalizationViewModel>()
            .AddSingleton<ThemePage>()
            .AddSingleton<RolePackagesPage>()
            .AddSingleton(provider => new PersonalizationPage(
                provider.GetRequiredService<PersonalizationViewModel>(),
                provider.GetRequiredService<ServantLibraryViewModel>(),
                provider.GetRequiredService<ISettingsNavigator>(), provider.GetRequiredService<ThemePage>()));
        services.AddSingleton<ISettingsPageViewFactory>(provider => new SettingsPageViewFactory(
            nameof(SettingsSection.Personalization), _ => provider.GetRequiredService<PersonalizationPage>(),
            "个性化", "调整应用的个性化偏好。", "开始使用", ["外观", "角色", "个性化"], 20));
        services.AddSingleton<ISettingsPageViewFactory>(provider => new SettingsPageViewFactory(
            nameof(SettingsSection.Theme), _ => provider.GetRequiredService<ThemePage>(),
            "主题", "选择界面的视觉主题。", "管理", ["亮色", "暗色", "主题"], 220));
        services.AddSingleton<ISettingsPageViewFactory>(provider => new SettingsPageViewFactory(
            nameof(SettingsSection.RolePackages), route => route.ItemId is null
                ? provider.GetRequiredService<RolePackagesPage>()
                : new RolePackageDetailPage(new RolePackageDetailViewModel(
                    new PackageDetailRoute(route.ItemId, route.DisplayName ?? route.ItemId),
                    provider.GetRequiredService<ServantLibraryViewModel>(),
                    provider.GetRequiredService<ICharacterSettingsStore>(),
                    provider.GetRequiredService<ISettingsNavigator>())),
            "角色包", "安装、浏览并管理角色包。", "能力", ["角色", "外观", "安装"], 110));
        return services;
    }
}
