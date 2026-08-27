using System.Security.Cryptography;
using FgoPet.Core.Packs;

namespace FgoPet.Infrastructure.Packs;

/// <summary>A structurally valid appearance whose declared files and hashes were verified.</summary>
public sealed record ValidatedAppearance(AppearanceManifestV3 Manifest, string Root);

public sealed record AppearanceValidationResult(ValidatedAppearance? Value, IReadOnlyList<PackFailure> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// Validates that every declared asset stays inside <paramref name="root"/>, exists,
/// and hashes to the manifest's SHA-256. Decoding, alpha, and pixel dimensions are
/// checked by the WPF layer that loads frozen snapshots.
/// </summary>
public static class AppearanceValidator
{
    public static AppearanceValidationResult Validate(AppearanceManifestV3 manifest, string root)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var fullRoot = Path.GetFullPath(root);
        var errors = new List<PackFailure>();

        foreach (var asset in manifest.Assets)
        {
            var fullPath = Path.GetFullPath(Path.Combine(fullRoot, asset.RelativePath));
            if (!IsWithin(fullPath, fullRoot))
            {
                errors.Add(new PackFailure(PackErrorCode.PackagePathEscapesRoot, "素材路径越出外观根目录。", asset.RelativePath));
                continue;
            }
            if (!File.Exists(fullPath))
            {
                errors.Add(new PackFailure(PackErrorCode.AssetMissing, $"素材文件缺失。", asset.RelativePath));
                continue;
            }

            byte[] content;
            try
            {
                content = File.ReadAllBytes(fullPath);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                errors.Add(new PackFailure(PackErrorCode.AssetMissing, $"无法读取素材文件: {error.Message}", asset.RelativePath));
                continue;
            }

            if (!HashesMatch(asset.Sha256, content))
            {
                errors.Add(new PackFailure(PackErrorCode.AssetHashMismatch, "SHA-256 与 manifest 不一致。", asset.RelativePath));
            }
        }

        return errors.Count == 0
            ? new AppearanceValidationResult(new ValidatedAppearance(manifest, fullRoot), errors)
            : new AppearanceValidationResult(null, errors);
    }

    private static bool IsWithin(string path, string root)
    {
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
               || path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HashesMatch(string manifestHash, byte[] content)
    {
        var expected = manifestHash.Trim();
        if (expected.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            expected = expected["sha256:".Length..];
        }

        var actual = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        return string.Equals(actual, expected, StringComparison.Ordinal);
    }
}