using FgoPet.Core.Portraits;

namespace FgoPet.Core.Packs;

public interface IArtPackageRepository
{
    Task<PackCatalog> ScanAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<InstalledServant>> ListServantsAsync(CancellationToken cancellationToken);

    Task<AppearanceLocation?> GetAppearanceAsync(PortraitSelection selection, CancellationToken cancellationToken);

    Task<AppearanceLocation?> ResolveStartupSelectionAsync(
        PortraitSelection? requested,
        CancellationToken cancellationToken);

    Task<bool> RemoveAsync(string packageId, string packageVersion, CancellationToken cancellationToken);

    Task MarkLastKnownGoodAsync(PortraitSelection selection, CancellationToken cancellationToken);
}

/// <summary>A servant with its browsable appearances across installed package versions.</summary>
public sealed record InstalledServant(
    string PackageId,
    string ServantId,
    string DisplayName,
    string? PreviewPath,
    string? Publisher,
    IReadOnlyList<ServantAppearance> Appearances);

public sealed record ServantAppearance(
    string AppearanceId,
    string PackageVersion,
    string AppearanceRoot,
    string? PreviewPath);