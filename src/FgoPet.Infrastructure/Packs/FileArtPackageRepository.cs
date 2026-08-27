using FgoPet.Core.Packs;
using FgoPet.Core.Portraits;

namespace FgoPet.Infrastructure.Packs;

/// <summary>
/// Discovers installed packs under <c>Packages/&lt;package-id&gt;/&lt;version&gt;</c>,
/// persists the preferred and last-known-good selections through an index store, and
/// resolves startup selections with the recovery order: current valid version, prior
/// valid version of the same package, last-known-good pack, then any valid pack.
/// </summary>
public sealed class FileArtPackageRepository : IArtPackageRepository
{
    private readonly string _packagesRoot;
    private readonly IPackIndexStore _index;

    public FileArtPackageRepository(string packagesRoot, IPackIndexStore index)
    {
        _packagesRoot = packagesRoot ?? throw new ArgumentNullException(nameof(packagesRoot));
        _index = index ?? throw new ArgumentNullException(nameof(index));
    }

    public Task<PackCatalog> ScanAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Scan(cancellationToken));

    public Task<IReadOnlyList<InstalledServant>> ListServantsAsync(CancellationToken cancellationToken)
    {
        var catalog = Scan(cancellationToken);
        var servants = catalog.Packs
            .GroupBy(pack => pack.ServantId, StringComparer.Ordinal)
            .Select(group =>
            {
                var best = group.OrderByDescending(pack => pack.Version).First();
                var appearances = group
                    .OrderByDescending(pack => pack.Version)
                    .SelectMany(pack => pack.Appearances.Select(slot => new ServantAppearance(
                        slot.AppearanceId,
                        pack.PackageVersion,
                        AppearanceRoot(pack.PackRoot, slot.ManifestPath),
                        pack.PreviewPath)))
                    .ToList();
                return new InstalledServant(
                    best.PackageId,
                    best.ServantId,
                    best.DisplayName,
                    best.PreviewPath,
                    best.Publisher,
                    appearances);
            })
            .OrderBy(servant => servant.DisplayName, StringComparer.Ordinal)
            .ToList();

        return Task.FromResult<IReadOnlyList<InstalledServant>>(servants);
    }

    public Task<AppearanceLocation?> GetAppearanceAsync(PortraitSelection selection, CancellationToken cancellationToken)
    {
        var catalog = Scan(cancellationToken);
        return Task.FromResult(ResolveExact(catalog, selection));
    }

    public Task<AppearanceLocation?> ResolveStartupSelectionAsync(
        PortraitSelection? requested,
        CancellationToken cancellationToken)
    {
        var catalog = Scan(cancellationToken);
        var index = _index.Load();

        var preferred = requested ?? index.Selected;
        if (preferred is not null)
        {
            var exact = ResolveExact(catalog, preferred);
            if (exact is not null)
            {
                return Task.FromResult<AppearanceLocation?>(exact);
            }

            var alternate = ResolveAlternateVersion(catalog, preferred);
            if (alternate is not null)
            {
                return Task.FromResult<AppearanceLocation?>(alternate);
            }
        }

        if (index.LastKnownGood is not null)
        {
            var lastKnown = ResolveExact(catalog, index.LastKnownGood)
                ?? ResolveAlternateVersion(catalog, index.LastKnownGood);
            if (lastKnown is not null)
            {
                return Task.FromResult<AppearanceLocation?>(lastKnown);
            }
        }

        var any = ResolveAny(catalog, preferred);
        return Task.FromResult(any);
    }

    public Task<bool> RemoveAsync(string packageId, string packageVersion, CancellationToken cancellationToken)
    {
        var catalog = Scan(cancellationToken);
        var index = _index.Load();
        var targetDir = Path.Combine(_packagesRoot, packageId, packageVersion);
        if (!Directory.Exists(targetDir))
        {
            return Task.FromResult(false);
        }

        var isRemovingCurrentSelection = index.Selected is not null
            && index.Selected.PackageId == packageId
            && (index.Selected.PackageVersion is null || index.Selected.PackageVersion == packageVersion);
        if (isRemovingCurrentSelection)
        {
            var otherValid = catalog.Packs.Any(pack =>
                pack.PackageId != packageId || pack.PackageVersion != packageVersion);
            if (otherValid)
            {
                // Switch-before-uninstall: staying on a removed pack would break recovery.
                return Task.FromResult(false);
            }

            // Final pack: uninstalling moves the app into the packless state.
            index = new PackIndexV1(Selected: null, LastKnownGood: null);
        }

        Directory.Delete(targetDir, recursive: true);
        var packageDir = Path.GetDirectoryName(targetDir)!;
        if (Directory.Exists(packageDir) && !Directory.EnumerateFileSystemEntries(packageDir).Any())
        {
            Directory.Delete(packageDir);
        }
        _index.Save(index);
        return Task.FromResult(true);
    }

    public Task MarkLastKnownGoodAsync(PortraitSelection selection, CancellationToken cancellationToken)
    {
        var index = _index.Load();
        _index.Save(new PackIndexV1(Selected: selection, LastKnownGood: selection));
        return Task.CompletedTask;
    }

    private PackCatalog Scan(CancellationToken cancellationToken)
    {
        var packs = new List<InstalledPack>();
        if (!Directory.Exists(_packagesRoot))
        {
            return new PackCatalog(packs);
        }

        foreach (var packageDir in Directory.EnumerateDirectories(_packagesRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var packageId = Path.GetFileName(packageDir);
            foreach (var versionDir in Directory.EnumerateDirectories(packageDir))
            {
                if (!SemVersion.TryParse(Path.GetFileName(versionDir), out var version))
                {
                    continue;
                }

                var manifest = TryReadPackageManifest(Path.Combine(versionDir, "package.json"));
                if (manifest is null || manifest.PackageId != packageId)
                {
                    // Malformed or mismatched identity: the directory is not a valid installed pack.
                    continue;
                }

                var slots = manifest.Appearances
                    .Select(appearance => new AppearanceSlot(appearance.AppearanceId, appearance.ManifestPath))
                    .ToArray();
                packs.Add(new InstalledPack(
                    packageId,
                    version.ToString(),
                    version,
                    versionDir,
                    manifest.ServantId,
                    manifest.DisplayName,
                    string.IsNullOrWhiteSpace(manifest.PreviewPath) ? null : manifest.PreviewPath,
                    string.IsNullOrWhiteSpace(manifest.Publisher) ? null : manifest.Publisher,
                    slots));
            }
        }

        packs.Sort((left, right) =>
        {
            var byPackage = string.CompareOrdinal(left.PackageId, right.PackageId);
            return byPackage != 0 ? byPackage : right.Version.CompareTo(left.Version);
        });
        return new PackCatalog(packs);
    }

    private PackManifestV1? TryReadPackageManifest(string path)
    {
        try
        {
            return PackJson.DeserializeStrict<PackManifestV1>(File.ReadAllText(path));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static AppearanceLocation? ResolveExact(PackCatalog catalog, PortraitSelection selection)
    {
        var pack = PickPack(catalog, selection.PackageId, selection.PackageVersion);
        var slot = pack?.Appearances.FirstOrDefault(appearance => appearance.AppearanceId == selection.AppearanceId);
        if (pack is null || slot is null)
        {
            return null;
        }

        var root = AppearanceRoot(pack.PackRoot, slot.ManifestPath);
        return Directory.Exists(root)
            ? new AppearanceLocation(new PackIdentity(pack.PackageId, pack.PackageVersion), slot.AppearanceId, root)
            : null;
    }

    private static AppearanceLocation? ResolveAlternateVersion(PackCatalog catalog, PortraitSelection selection)
    {
        var pack = catalog.ForPackage(selection.PackageId)
            .Where(candidate => selection.PackageVersion is null || candidate.PackageVersion != selection.PackageVersion)
            .OrderByDescending(candidate => candidate.Version)
            .FirstOrDefault();
        if (pack is null)
        {
            return null;
        }

        var slot = pack.Appearances.FirstOrDefault(appearance => appearance.AppearanceId == selection.AppearanceId)
            ?? pack.Appearances.FirstOrDefault();
        if (slot is null)
        {
            return null;
        }

        var root = AppearanceRoot(pack.PackRoot, slot.ManifestPath);
        return Directory.Exists(root)
            ? new AppearanceLocation(new PackIdentity(pack.PackageId, pack.PackageVersion), slot.AppearanceId, root)
            : null;
    }

    private static AppearanceLocation? ResolveAny(PackCatalog catalog, PortraitSelection? preferred)
    {
        var ordered = catalog.Packs
            .OrderByDescending(pack => preferred is not null && pack.PackageId == preferred.PackageId ? 1 : 0)
            .ThenByDescending(pack => pack.Version);
        foreach (var pack in ordered)
        {
            var slot = pack.Appearances.FirstOrDefault();
            if (slot is null)
            {
                continue;
            }

            var root = AppearanceRoot(pack.PackRoot, slot.ManifestPath);
            if (Directory.Exists(root))
            {
                return new AppearanceLocation(new PackIdentity(pack.PackageId, pack.PackageVersion), slot.AppearanceId, root);
            }
        }

        return null;
    }

    private static InstalledPack? PickPack(PackCatalog catalog, string packageId, string? packageVersion)
    {
        var candidates = catalog.Packs.Where(pack => pack.PackageId == packageId).ToArray();
        if (candidates.Length == 0)
        {
            return null;
        }

        return packageVersion is null
            ? candidates.OrderByDescending(pack => pack.Version).First()
            : candidates.FirstOrDefault(pack => pack.PackageVersion == packageVersion);
    }

    private static string AppearanceRoot(string packRoot, string manifestPath) =>
        Path.GetDirectoryName(Path.GetFullPath(Path.Combine(packRoot, manifestPath)))!;
}