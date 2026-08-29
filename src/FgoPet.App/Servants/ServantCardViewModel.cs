using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FgoPet.App.Servants;

/// <summary>Library card for one servant/package.</summary>
public sealed partial class ServantCardViewModel : ObservableObject
{
    public ServantCardViewModel(
        string packageId,
        string servantId,
        string displayName,
        string sourceBadge,
        bool isEmbedded,
        IReadOnlyList<ServantAppearanceItemViewModel> appearances)
        : this(
            packageId,
            servantId,
            displayName,
            sourceBadge,
            isEmbedded,
            appearances.FirstOrDefault()?.PackageVersion ?? string.Empty,
            appearances.FirstOrDefault()?.PreviewPath,
            null,
            [],
            appearances)
    {
    }

    public ServantCardViewModel(
        string packageId,
        string servantId,
        string displayName,
        string sourceBadge,
        bool isEmbedded,
        string packageVersion,
        string? previewSource,
        string? minAppVersion,
        IReadOnlyList<Core.Packs.PackSettingDefinition> settings,
        IReadOnlyList<ServantAppearanceItemViewModel> appearances)
    {
        PackageId = packageId;
        ServantId = servantId;
        DisplayName = displayName;
        SourceBadge = sourceBadge;
        IsEmbedded = isEmbedded;
        PackageVersion = packageVersion;
        PreviewSource = previewSource;
        MinAppVersion = minAppVersion;
        Settings = settings;
        Appearances = new(appearances);
        SelectedAppearance = Appearances.FirstOrDefault();
    }

    public string PackageId { get; }

    public string ServantId { get; }

    public string DisplayName { get; }

    public string SourceBadge { get; }

    public bool IsEmbedded { get; }

    public string PackageVersion { get; }

    public string? PreviewSource { get; }

    public string? MinAppVersion { get; }

    public IReadOnlyList<Core.Packs.PackSettingDefinition> Settings { get; }

    public ObservableCollection<ServantAppearanceItemViewModel> Appearances { get; }

    [ObservableProperty]
    private ServantAppearanceItemViewModel? _selectedAppearance;

    [ObservableProperty]
    private bool _isActive;

    public void RefreshFrom(ServantCardViewModel other)
    {
        Appearances.Clear();
        foreach (var appearance in other.Appearances)
        {
            Appearances.Add(appearance);
        }
        SelectedAppearance = Appearances.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedAppearance));
    }
}
