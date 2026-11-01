namespace FgoPet.Plugin.SocialSources;

/// <summary>
/// Owns exactly one grant file per platform. Keeping them separate means an approval recorded for one
/// service can never be read as approval for another, and a corrupt file cannot disable the rest.
/// </summary>
public sealed class SocialSourceSettingsStores(string directory)
{
    public SocialSourceSettingsStore Bilibili { get; } = new(directory, SocialSourceSettings.Bilibili);
    public SocialSourceSettingsStore Xiaohongshu { get; } = new(directory, SocialSourceSettings.Xiaohongshu);
    public SocialSourceSettingsStore Xiaoheihe { get; } = new(directory, SocialSourceSettings.Xiaoheihe);

    /// <summary>Optional authenticated session grants. Independent from the anonymous read grants.</summary>
    public SocialSessionSettingsStore BilibiliSession { get; } = new(directory, SocialSourceSettings.Bilibili);
    public SocialSessionSettingsStore XiaohongshuSession { get; } = new(directory, SocialSourceSettings.Xiaohongshu);
    public SocialSessionSettingsStore XiaoheiheSession { get; } = new(directory, SocialSourceSettings.Xiaoheihe);
}
