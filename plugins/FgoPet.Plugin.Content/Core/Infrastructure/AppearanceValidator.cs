using System.Security.Cryptography;
using System.Text.Json;
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
        => ValidateCore(manifest, root, includeLive2D: true);

    /// <summary>Loads the mandatory static fallback even when optional Live2D data is unavailable later.</summary>
    public static AppearanceValidationResult ValidateStatic(AppearanceManifestV3 manifest, string root)
        => ValidateCore(manifest, root, includeLive2D: false);

    private static AppearanceValidationResult ValidateCore(AppearanceManifestV3 manifest, string root, bool includeLive2D)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var fullRoot = Path.GetFullPath(root);
        var errors = new List<PackFailure>();

        foreach (var asset in manifest.Assets)
        {
            ValidateFile(fullRoot, asset.RelativePath, asset.Sha256, errors);
        }

        if (includeLive2D && manifest.Live2D is { } live2d)
        {
            ValidateLive2D(fullRoot, live2d, errors);
        }

        return errors.Count == 0
            ? new AppearanceValidationResult(new ValidatedAppearance(manifest, fullRoot), errors)
            : new AppearanceValidationResult(null, errors);
    }

    private static void ValidateLive2D(string root, Live2DAppearanceV1 live2d, List<PackFailure> errors)
    {
        if (!IsSafeRelativePath(live2d.ModelPath) || !live2d.ModelPath.EndsWith(".model3.json", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(new PackFailure(PackErrorCode.PackagePathEscapesRoot, "无效的 Live2D 模型路径。", live2d.ModelPath));
            return;
        }

        foreach (var file in live2d.Files)
        {
            if (!IsSafeRelativePath(file.RelativePath))
            {
                errors.Add(new PackFailure(PackErrorCode.PackagePathEscapesRoot, "无效的 Live2D 资源路径。", file.RelativePath));
                continue;
            }
            if (!IsLive2DDataFile(file.RelativePath))
            {
                errors.Add(new PackFailure(PackErrorCode.ManifestMalformed, "Live2D 角色包只能包含已知运行时数据文件。", file.RelativePath));
                continue;
            }
            ValidateFile(root, file.RelativePath, file.Sha256, errors);
        }
        if (errors.Count > 0)
        {
            return;
        }

        var modelPath = Path.Combine(root, live2d.ModelPath.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            using var model = JsonDocument.Parse(File.ReadAllBytes(modelPath));
            var references = ReadModelReferences(model.RootElement);
            var declared = live2d.Files.Select(file => file.RelativePath).ToHashSet(StringComparer.Ordinal);
            var expected = new HashSet<string>(StringComparer.Ordinal) { live2d.ModelPath };
            var modelDirectory = Path.GetDirectoryName(live2d.ModelPath.Replace('/', Path.DirectorySeparatorChar)) ?? string.Empty;
            foreach (var reference in references)
            {
                if (!IsSafeRelativePath(reference))
                {
                    errors.Add(new PackFailure(PackErrorCode.PackagePathEscapesRoot, "模型资源引用不是安全相对路径。", reference));
                    return;
                }
                var path = Path.Combine(modelDirectory, reference.Replace('/', Path.DirectorySeparatorChar)).Replace('\\', '/');
                expected.Add(path);
            }
            if (!expected.SetEquals(declared))
            {
                errors.Add(new PackFailure(PackErrorCode.ManifestMalformed, "Live2D 资源声明必须与 model3.json 引用完全一致。", live2d.ModelPath));
            }
        }
        catch (Exception error) when (error is JsonException or IOException or InvalidOperationException or KeyNotFoundException)
        {
            errors.Add(new PackFailure(PackErrorCode.ManifestMalformed, $"无效的 Live2D model3.json: {error.Message}", live2d.ModelPath));
        }
    }

    private static IReadOnlyList<string> ReadModelReferences(JsonElement model)
    {
        if (model.GetProperty("Version").GetInt32() != 3)
        {
            throw new JsonException("仅支持 Cubism model3.json Version 3。");
        }
        var fileReferences = model.GetProperty("FileReferences");
        var paths = new List<string>();
        foreach (var field in fileReferences.EnumerateObject())
        {
            switch (field.Name)
            {
                case "Moc":
                case "Physics":
                case "Pose":
                case "DisplayInfo":
                case "UserData":
                    paths.Add(field.Value.GetString() ?? throw new JsonException("空资源路径。"));
                    break;
                case "Textures":
                    paths.AddRange(field.Value.EnumerateArray().Select(item => item.GetString() ?? throw new JsonException("空贴图路径。")));
                    break;
                case "Motions":
                    foreach (var group in field.Value.EnumerateObject())
                    {
                        foreach (var motion in group.Value.EnumerateArray())
                        {
                            paths.Add(motion.GetProperty("File").GetString() ?? throw new JsonException("空动作路径。"));
                            if (motion.TryGetProperty("Sound", out _))
                            {
                                throw new JsonException("角色包暂不支持动作音频。");
                            }
                        }
                    }
                    break;
                case "Expressions":
                    paths.AddRange(field.Value.EnumerateArray().Select(item => item.GetProperty("File").GetString() ?? throw new JsonException("空表情路径。")));
                    break;
                default:
                    throw new JsonException($"未支持的模型资源类型: {field.Name}。");
            }
        }
        if (!fileReferences.TryGetProperty("Moc", out _) || !fileReferences.TryGetProperty("Textures", out _))
        {
            throw new JsonException("模型缺少 Moc 或 Textures。");
        }
        return paths;
    }

    private static bool IsLive2DDataFile(string path) =>
        new[] { ".model3.json", ".moc3", ".png", ".physics3.json", ".motion3.json", ".exp3.json", ".cdi3.json", ".pose3.json", ".userdata3.json" }
            .Any(extension => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

    private static bool IsSafeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith("/", StringComparison.Ordinal) || path.Contains('\\')
            || (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':'))
        {
            return false;
        }
        return path.Split('/').All(segment => segment.Length > 0 && segment is not "." and not "..");
    }

    private static void ValidateFile(string root, string relativePath, string hash, List<PackFailure> errors)
    {
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!IsWithin(fullPath, root))
        {
            errors.Add(new PackFailure(PackErrorCode.PackagePathEscapesRoot, "素材路径越出外观根目录。", relativePath));
            return;
        }
        byte[] content;
        try
        {
            content = File.ReadAllBytes(fullPath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            errors.Add(new PackFailure(PackErrorCode.AssetMissing, $"无法读取素材文件: {error.Message}", relativePath));
            return;
        }
        if (!HashesMatch(hash, content))
        {
            errors.Add(new PackFailure(PackErrorCode.AssetHashMismatch, "SHA-256 与 manifest 不一致。", relativePath));
        }
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
