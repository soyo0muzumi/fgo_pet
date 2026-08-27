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
    {
        PackageId = packageId;
        ServantId = servantId;
        DisplayName = displayName;
        SourceBadge = sourceBadge;
        IsEmbedded = isEmbedded;
        Appearances = new(appearances);
        SelectedAppearance = Appearances.FirstOrDefault();
    }

    public string PackageId { get; }

    public string ServantId { get; }

    public string DisplayName { get; }

    public string SourceBadge { get; }

    public bool IsEmbedded { get; }

    public ObservableCollection<ServantAppearanceItemViewModel> Appearances { get; }

    public ServantAppearanceItemViewModel? SelectedAppearance { get; set; }

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