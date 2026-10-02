using FgoPet.Core.Packs;

namespace FgoPet.Infrastructure.Packs;

/// <summary>Reads and strictly parses an art schema v3 appearance manifest from disk.</summary>
public static class AppearanceManifestReader
{
    public static AppearanceManifestV3 Read(string absoluteManifestPath)
    {
        if (string.IsNullOrWhiteSpace(absoluteManifestPath) || !Path.IsPathFullyQualified(absoluteManifestPath))
        {
            throw new PackFailureException(new PackFailure(
                PackErrorCode.ManifestMalformed,
                "外观 manifest 路径必须是绝对路径。",
                absoluteManifestPath));
        }

        if (!File.Exists(absoluteManifestPath))
        {
            throw new PackFailureException(new PackFailure(
                PackErrorCode.AssetMissing,
                "外观 manifest 文件不存在。",
                absoluteManifestPath));
        }

        string json;
        try
        {
            json = File.ReadAllText(absoluteManifestPath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new PackFailureException(new PackFailure(
                PackErrorCode.ManifestMalformed,
                $"无法读取外观 manifest: {error.Message}",
                absoluteManifestPath));
        }

        return PackJson.DeserializeStrict<AppearanceManifestV3>(json, absoluteManifestPath);
    }
}