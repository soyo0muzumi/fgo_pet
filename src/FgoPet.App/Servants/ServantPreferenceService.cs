using FgoPet.Core.Settings;

namespace FgoPet.App.Servants;

/// <summary>Reads and writes servant address preferences without coupling them to login.</summary>
public sealed class ServantPreferenceService
{
    private readonly IAppSettingsStore _settings;

    public ServantPreferenceService(IAppSettingsStore settings) =>
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

    public Task<ServantPreference> LoadAsync(string servantId)
    {
        var normalizedServantId = NormalizeServantId(servantId);
        var settings = _settings.Load();
        return Task.FromResult(settings.ServantPreferences.TryGetValue(normalizedServantId, out var preference)
            ? preference
            : new ServantPreference(AddressMode.PackageDefault));
    }

    public Task SaveAsync(string servantId, ServantPreference preference)
    {
        var normalizedServantId = NormalizeServantId(servantId);
        ArgumentNullException.ThrowIfNull(preference);
        var settings = _settings.Load();
        var preferences = settings.ServantPreferences.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        preferences[normalizedServantId] = preference;
        _settings.Save(settings with { ServantPreferences = preferences });
        return Task.CompletedTask;
    }

    public async Task<string> ResolveAddressAsync(
        string servantId,
        string? appearanceDefault,
        string? personaDefault)
    {
        var preference = await LoadAsync(servantId);
        if (preference.AddressMode == AddressMode.UserDefined && !string.IsNullOrWhiteSpace(preference.AddressText))
        {
            return preference.AddressText!;
        }

        return FirstNonEmpty(appearanceDefault, personaDefault) ?? "御主";
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string NormalizeServantId(string servantId)
    {
        if (string.IsNullOrWhiteSpace(servantId) || servantId.Trim().Length > 128)
        {
            throw new ArgumentException("A valid servant ID is required.", nameof(servantId));
        }

        return servantId.Trim();
    }
}
