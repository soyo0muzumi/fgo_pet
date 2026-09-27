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
            nameof(SettingsSection.Personalization), _ => provider.GetRequiredService<PersonalizationPage>()));
        services.AddSingleton<ISettingsPageViewFactory>(provider => new SettingsPageViewFactory(
            nameof(SettingsSection.Theme), _ => provider.GetRequiredService<ThemePage>()));
        services.AddSingleton<ISettingsPageViewFactory>(provider => new SettingsPageViewFactory(
            nameof(SettingsSection.RolePackages), route => route.ItemId is null
                ? provider.GetRequiredService<RolePackagesPage>()
                : new RolePackageDetailPage(new RolePackageDetailViewModel(
                    new PackageDetailRoute(route.ItemId, route.DisplayName ?? route.ItemId),
                    provider.GetRequiredService<ServantLibraryViewModel>(),
                    provider.GetRequiredService<ICharacterSettingsStore>(),
                    provider.GetRequiredService<ISettingsNavigator>()))));
        return services;
    }
}
