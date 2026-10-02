namespace FgoPet.App.Settings;

public sealed record SettingsNavigationGroup(
    string Label,
    IReadOnlyList<SettingsNavigationItem> Items);
