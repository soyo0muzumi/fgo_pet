using CommunityToolkit.Mvvm.ComponentModel;

namespace FgoPet.App.Servants;

/// <summary>One installable appearance of a servant for the library list.</summary>
public sealed partial class ServantAppearanceItemViewModel : ObservableObject
{
    public ServantAppearanceItemViewModel(string appearanceId, string packageVersion, string? previewPath)
    {
        AppearanceId = appearanceId;
        PackageVersion = packageVersion;
        PreviewPath = previewPath;
    }

    public string AppearanceId { get; }

    public string PackageVersion { get; }

    public string? PreviewPath { get; }

    public string Display => $"{AppearanceId} (v{PackageVersion})";

    [ObservableProperty]
    private bool _isCurrent;
}
