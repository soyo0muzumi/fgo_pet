namespace FgoPet.Core.Packs;

/// <summary>Stable program version used for pack compatibility checks.</summary>
public static class FgoPetAppVersion
{
    public static readonly SemVersion Current = SemVersion.Parse("1.0.0");
}

public sealed record PackIdentity(string PackageId, string PackageVersion);

public sealed record PackInstallResult(bool Installed, PackIdentity? Identity, PackFailure? Failure);

public interface IPackInstaller
{
    Task<PackInstallResult> InstallAsync(string archivePath, CancellationToken cancellationToken);
}