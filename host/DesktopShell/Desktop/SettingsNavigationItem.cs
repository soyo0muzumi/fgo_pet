namespace FgoPet.App.Settings;

public sealed record SettingsNavigationItem(
    SettingsSection Section,
    string Label,
    string Description,
    string IconKey);
