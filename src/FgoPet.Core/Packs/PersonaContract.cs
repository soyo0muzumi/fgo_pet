using FgoPet.Core.Dialogue;

namespace FgoPet.Core.Packs;

public sealed record PersonaAppearanceOverlay
{
    public PersonaAppearanceOverlay(string appearanceId, string text, string? defaultAddress = null)
    {
        AppearanceId = Phase3Validation.Id(appearanceId, nameof(appearanceId));
        Text = Phase3Validation.Text(text, nameof(text), 8_000);
        DefaultAddress = string.IsNullOrWhiteSpace(defaultAddress)
            ? null
            : Phase3Validation.Text(defaultAddress, nameof(defaultAddress), 128);
    }

    public string AppearanceId { get; }
    public string Text { get; }
    public string? DefaultAddress { get; }
}

public sealed record PersonaBundle
{
    public PersonaBundle(
        string servantId,
        string packageId,
        string packageVersion,
        string personaVersion,
        string coreText,
        IReadOnlyList<PersonaAppearanceOverlay> appearanceOverlays,
        string? defaultAddress = null)
    {
        ServantId = Phase3Validation.Id(servantId, nameof(servantId));
        PackageId = Phase3Validation.Id(packageId, nameof(packageId));
        PackageVersion = Phase3Validation.Id(packageVersion, nameof(packageVersion), 64);
        PersonaVersion = Phase3Validation.Id(personaVersion, nameof(personaVersion), 64);
        CoreText = Phase3Validation.Text(coreText, nameof(coreText), 16_000);
        AppearanceOverlays = appearanceOverlays is null
            ? throw new ArgumentNullException(nameof(appearanceOverlays))
            : appearanceOverlays.ToArray();
        DefaultAddress = string.IsNullOrWhiteSpace(defaultAddress)
            ? null
            : Phase3Validation.Text(defaultAddress, nameof(defaultAddress), 128);
    }

    public string ServantId { get; }
    public string PackageId { get; }
    public string PackageVersion { get; }
    public string PersonaVersion { get; }
    public string CoreText { get; }
    public IReadOnlyList<PersonaAppearanceOverlay> AppearanceOverlays { get; }
    public string? DefaultAddress { get; }

    public PersonaAppearanceOverlay? FindAppearance(string appearanceId) =>
        AppearanceOverlays.FirstOrDefault(item =>
            string.Equals(item.AppearanceId, appearanceId, StringComparison.Ordinal));
}
