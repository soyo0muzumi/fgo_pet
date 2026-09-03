using System.IO.Compression;
using System.Text;
using FgoPet.Core.Packs;
using FgoPet.Infrastructure.FileSystem;

namespace FgoPet.Infrastructure.Packs;

/// <summary>
/// Installs a <c>.fgopetpack</c> archive transactionally: pre-checks the archive
/// structure, extracts to a random staging directory, strictly parses and validates
/// every manifest, and atomically moves the result to
/// <c>Packages/&lt;package-id&gt;/&lt;version&gt;</c>. An existing version is never
/// overwritten, and any failure removes the staging directory while leaving installed
/// packs untouched.
/// </summary>
public sealed class FgoPetPackInstaller : IPackInstaller
{
    private readonly PackArchivePolicy _policy;
    private readonly string _packagesRoot;
    private readonly string _stagingRoot;
    private readonly SemVersion _currentAppVersion;
    private readonly IAtomicDirectoryMover _mover;

    public FgoPetPackInstaller(
        PackArchivePolicy policy,
        string packagesRoot,
        string storageRoot,
        SemVersion currentAppVersion,
        IAtomicDirectoryMover mover)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _packagesRoot = packagesRoot ?? throw new ArgumentNullException(nameof(packagesRoot));
        _stagingRoot = Path.Combine(storageRoot ?? throw new ArgumentNullException(nameof(storageRoot)), "Staging");
        _currentAppVersion = currentAppVersion ?? throw new ArgumentNullException(nameof(currentAppVersion));
        _mover = mover ?? throw new ArgumentNullException(nameof(mover));
    }

    public Task<PackInstallResult> InstallAsync(string archivePath, CancellationToken cancellationToken) =>
        Task.FromResult(InstallCore(archivePath, cancellationToken));

    private PackInstallResult InstallCore(string archivePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DirectoryInfo? staging = null;
        try
        {
            if (string.IsNullOrWhiteSpace(archivePath) || !Path.IsPathFullyQualified(archivePath))
            {
                return Failed(PackErrorCode.PackageArchiveInvalid, "存档路径必须是绝对路径。");
            }
            if (!File.Exists(archivePath))
            {
                return Failed(PackErrorCode.PackageArchiveInvalid, "存档文件不存在。");
            }

            bool hasPackageManifest;
            using (var archive = TryOpen(archivePath, out var openFailure))
            {
                if (archive is null)
                {
                    return openFailure!;
                }

                if (!TryValidateStructure(archive, out var structureFailure, out hasPackageManifest))
                {
                    return structureFailure!;
                }
                if (!hasPackageManifest)
                {
                    return Failed(PackErrorCode.ManifestMalformed, "存档缺少根级 package.json。");
                }

                staging = CreateStagingDirectory();
                Extract(archive, staging.FullName);
            }

            PackManifestV1 manifest;
            try
            {
                manifest = ReadPackManifest(Path.Combine(staging.FullName, "package.json"));
            }
            catch (PackFailureException failureException)
            {
                CleanupStaging(staging);
                return new PackInstallResult(false, null, failureException.Failure);
            }
            var compatibility = CheckCompatibility(manifest);
            if (compatibility is not null)
            {
                return FailStaging(staging, compatibility);
            }

            var identity = new PackIdentity(manifest.PackageId, manifest.PackageVersion);
            var target = Path.Combine(
                _packagesRoot,
                SanitizeDirectoryName(manifest.PackageId),
                manifest.PackageVersion);
            if (Directory.Exists(target))
            {
                return FailStaging(staging, Failed(PackErrorCode.PackageArchiveInvalid, $"版本 {manifest.PackageVersion} 已安装，不会覆盖已有版本。"));
            }

            var treeFailure = ValidateInstalledTree(staging.FullName, manifest);
            if (treeFailure is not null)
            {
                return FailStaging(staging, treeFailure);
            }

            Directory.CreateDirectory(_packagesRoot);
            _mover.Move(staging.FullName, target);
            staging = null;
            return new PackInstallResult(true, identity, null);
        }
        catch (OperationCanceledException)
        {
            CleanupStaging(staging);
            throw;
        }
        catch (Exception error)
        {
            CleanupStaging(staging);
            return Failed(PackErrorCode.PackageArchiveInvalid, error.Message);
        }
    }

    private ZipArchive? TryOpen(string archivePath, out PackInstallResult? failure)
    {
        try
        {
            failure = null;
            return ZipFile.OpenRead(archivePath);
        }
        catch (Exception error)
        {
            failure = Failed(PackErrorCode.PackageArchiveInvalid, $"无法打开存档: {error.Message}");
            return null;
        }
    }

    private bool TryValidateStructure(ZipArchive archive, out PackInstallResult? failure, out bool hasPackageManifest)
    {
        failure = null;
        hasPackageManifest = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        long totalExpanded = 0;

        foreach (var entry in archive.Entries)
        {
            if (!TryNormalize(entry.FullName, out var normalized, out var reason))
            {
                failure = Failed(PackErrorCode.PackagePathEscapesRoot, reason ?? "路径非法。", entry.FullName);
                return false;
            }

            var isDirectory = normalized.EndsWith("/");
            var key = isDirectory ? normalized.TrimEnd('/') : normalized;
            if (key.Length == 0)
            {
                continue;
            }

            if (!isDirectory && key.Equals("package.json", StringComparison.Ordinal))
            {
                hasPackageManifest = true;
            }

            var allowedExtension = isDirectory
                || _policy.AllowedExtensions.Contains(Path.GetExtension(key).ToLowerInvariant());
            if (!allowedExtension)
            {
                failure = Failed(PackErrorCode.PackageArchiveInvalid, $"不允许的文件类型: {entry.FullName}");
                return false;
            }

            if (!seen.Add(key))
            {
                failure = Failed(PackErrorCode.PackageArchiveInvalid, $"重复的路径: {entry.FullName}");
                return false;
            }

            if (entry.Length > _policy.MaxEntryBytes)
            {
                failure = Failed(PackErrorCode.PackageTooLarge, $"单个条目超过上限: {entry.FullName}");
                return false;
            }

            totalExpanded += entry.Length;
            if (totalExpanded > _policy.MaxExpandedBytes)
            {
                failure = Failed(PackErrorCode.PackageTooLarge, "解压后总大小超过上限。");
                return false;
            }
        }

        if (archive.Entries.Count > _policy.MaxEntries)
        {
            failure = Failed(PackErrorCode.PackageTooLarge, "条目数量超过上限。");
            return false;
        }

        return true;
    }

    private static bool TryNormalize(string rawName, out string normalized, out string? reason)
        {
            normalized = rawName.Replace('\\', '/');
            if (normalized.StartsWith('/'))
            {
                reason = "绝对路径条目。";
                return false;
            }
            normalized = RegexCollapseSlashes(normalized).TrimStart('/');

        if (normalized.Length == 0)
        {
            reason = "空条目名。";
            return false;
        }

        foreach (var segment in normalized.Split('/'))
        {
            if (segment == "..")
            {
                reason = "路径越界条目。";
                return false;
            }
        }

        if (normalized.Length >= 2 && char.IsAsciiLetter(normalized[0]) && normalized[1] == ':')
        {
            reason = "带盘符的绝对路径条目。";
            return false;
        }

        reason = null;
        return true;
    }

    private static string RegexCollapseSlashes(string value)
    {
        var builder = new StringBuilder(value.Length);
        var previousSlash = false;
        foreach (var character in value)
        {
            var isSlash = character == '/';
            if (isSlash && previousSlash)
            {
                continue;
            }
            builder.Append(character);
            previousSlash = isSlash;
        }
        return builder.ToString();
    }

    private DirectoryInfo CreateStagingDirectory()
    {
        Directory.CreateDirectory(_stagingRoot);
        return Directory.CreateDirectory(Path.Combine(_stagingRoot, Guid.NewGuid().ToString("N")));
    }

    private static void Extract(ZipArchive archive, string stagingRoot)
    {
        var fullStage = Path.GetFullPath(stagingRoot);
        foreach (var entry in archive.Entries)
        {
            if (!TryNormalize(entry.FullName, out var normalized, out var reason))
            {
                throw new InvalidDataException(reason ?? "非法条目。");
            }

            var target = Path.GetFullPath(Path.Combine(fullStage, normalized));
            if (!IsWithin(target, fullStage))
            {
                throw new InvalidDataException($"条目越出 staging: {entry.FullName}");
            }

            if (normalized.EndsWith("/"))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            var directory = Path.GetDirectoryName(target)!;
            Directory.CreateDirectory(directory);
            using (var destination = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var source = entry.Open())
            {
                source.CopyTo(destination);
            }
        }
    }

    private PackManifestV1 ReadPackManifest(string path)
    {
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new PackFailureException(new PackFailure(
                PackErrorCode.ManifestMalformed,
                $"无法读取 package.json: {error.Message}",
                "package.json"));
        }

        return PackJson.DeserializeStrict<PackManifestV1>(json, "package.json");
    }

    private PackInstallResult? CheckCompatibility(PackManifestV1 manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.MinAppVersion))
        {
            return null;
        }

        if (!SemVersion.TryParse(manifest.MinAppVersion, out var minimum))
        {
            return Failed(PackErrorCode.ManifestMalformed, $"min_app_version '{manifest.MinAppVersion}' 不是有效的 SemVer。");
        }

        return minimum.CompareTo(_currentAppVersion) > 0
            ? Failed(PackErrorCode.AppVersionIncompatible, $"此包要求 FgoPet {minimum}，当前为 {_currentAppVersion}。")
            : null;
    }

    private PackInstallResult? ValidateInstalledTree(string extractionRoot, PackManifestV1 manifest)
    {
        var fullRoot = Path.GetFullPath(extractionRoot);

        var declaredFilesFailure = ValidateDeclaredFiles(fullRoot, manifest);
        if (declaredFilesFailure is not null)
        {
            return declaredFilesFailure;
        }

        if (!string.IsNullOrWhiteSpace(manifest.PreviewPath))
        {
            var preview = Path.GetFullPath(Path.Combine(fullRoot, manifest.PreviewPath));
            if (!IsWithin(preview, fullRoot))
            {
                return Failed(PackErrorCode.PackagePathEscapesRoot, "预览路径越出包根目录。", manifest.PreviewPath);
            }
            if (!File.Exists(preview))
            {
                return Failed(PackErrorCode.AssetMissing, "从者库预览图缺失。", manifest.PreviewPath);
            }
        }

        foreach (var appearance in manifest.Appearances)
        {
            var manifestPath = Path.GetFullPath(Path.Combine(fullRoot, appearance.ManifestPath));
            if (!IsWithin(manifestPath, fullRoot))
            {
                return Failed(PackErrorCode.PackagePathEscapesRoot, "外观 manifest 路径越出包根目录。", appearance.ManifestPath);
            }

            AppearanceManifestV3 appearanceManifest;
            try
            {
                appearanceManifest = AppearanceManifestReader.Read(manifestPath);
            }
            catch (PackFailureException error)
            {
                return new PackInstallResult(false, null, error.Failure);
            }

            var appearanceRoot = Path.GetDirectoryName(manifestPath)!;
            var validation = AppearanceValidator.Validate(appearanceManifest, appearanceRoot);
            if (!validation.IsValid)
            {
                return new PackInstallResult(false, null, validation.Errors[0]);
            }
        }

        return null;
    }

    private static PackInstallResult? ValidateDeclaredFiles(string fullRoot, PackManifestV1 manifest)
    {
        if (manifest.Files is null or { Count: 0 })
        {
            return null;
        }

        var declared = new HashSet<string>(manifest.Files, StringComparer.Ordinal)
        {
            "package.json",
        };
        foreach (var relative in manifest.Files)
        {
            var path = Path.GetFullPath(Path.Combine(fullRoot, relative));
            if (!IsWithin(path, fullRoot))
            {
                return Failed(PackErrorCode.PackagePathEscapesRoot, "声明文件路径越出包根目录。", relative);
            }
            if (!File.Exists(path))
            {
                return Failed(PackErrorCode.AssetMissing, "声明文件缺失。", relative);
            }
        }

        foreach (var path in Directory.EnumerateFiles(fullRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(fullRoot, path).Replace('\\', '/');
            if (!declared.Contains(relative))
            {
                return Failed(PackErrorCode.PackageArchiveInvalid, "存在未声明的包文件。", relative);
            }
        }
        return null;
    }

    private static bool IsWithin(string path, string root)
    {
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
               || path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string SanitizeDirectoryName(string packageId) =>
        new(packageId.Select(character => char.IsLetterOrDigit(character) || character is '.' or '-' or '_' ? character : '_').ToArray());

    private static PackInstallResult FailStaging(DirectoryInfo? staging, PackInstallResult failure)
    {
        CleanupStaging(staging);
        return failure;
    }

    private static PackInstallResult Failed(PackErrorCode code, string message, string? relativePath = null)
        => new(false, null, new PackFailure(code, message, relativePath));

    private static void CleanupStaging(DirectoryInfo? staging)
    {
        if (staging is null || !staging.Exists)
        {
            return;
        }

        try
        {
            Directory.Delete(staging.FullName, recursive: true);
        }
        catch (Exception)
        {
            // best effort cleanup; a leftover staging directory is ignored by scans
        }
    }
}
