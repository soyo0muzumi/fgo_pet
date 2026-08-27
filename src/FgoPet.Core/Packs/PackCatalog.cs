namespace FgoPet.Core.Packs;

public sealed record AppearanceSlot(string AppearanceId, string ManifestPath);

/// <summary>One installed package version discovered by a repository scan.</summary>
public sealed record InstalledPack(
    string PackageId,
    string PackageVersion,
    SemVersion Version,
    string PackRoot,
    string ServantId,
    string DisplayName,
    string? PreviewPath,
    string? Publisher,
    IReadOnlyList<AppearanceSlot> Appearances);

public sealed record PackCatalog(IReadOnlyList<InstalledPack> Packs)
{
    public IReadOnlyList<InstalledPack> ForPackage(string packageId) =>
        Packs.Where(pack => pack.PackageId == packageId).ToArray();
}