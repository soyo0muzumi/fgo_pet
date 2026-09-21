using CommunityToolkit.Mvvm.Input;

namespace FgoPet.App.Settings;

/// <summary>
/// Navigates the settings shell to a section and exposes the shell-level commands
/// that packages views bind to (opening a package detail, returning to the package
/// list, returning to the appearance page).
///
/// This exists so that modules can offer "open settings here" affordances without
/// depending on the shell's concrete view model (which lives in the Desktop layer
/// and would otherwise invert the host/module dependency direction).
/// </summary>
public interface ISettingsNavigator
{
    void Select(SettingsSection section);

    IRelayCommand<PackageDetailRoute> OpenPackageCommand { get; }

    IRelayCommand BackToPackagesCommand { get; }

    IRelayCommand BackToAppearanceCommand { get; }
}
