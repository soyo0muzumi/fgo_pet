using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FgoPet.Core.Packs;

namespace FgoPet.Infrastructure.Packs;

/// <summary>Reads optional JSONL Knowledge and exposes only approved entries.</summary>
public static class KnowledgeManifestReader
{
    public const int SchemaVersion = 1;

    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
    };

    public static IReadOnlyList<KnowledgeEntry>? ReadOptional(
        string packageRoot,
        string servantId,
        string appearanceId)
    {
        try
        {
            var manifest = ReadManifest(packageRoot);
            if (manifest is null)
            {
                return null;
            }

            var entries = new List<KnowledgeEntry>();
            foreach (var file in manifest.Files)
            {
                ReadEntries(packageRoot, file, servantId, appearanceId, entries);
            }

            return entries
                .OrderBy(entry => entry.Rank ?? int.MaxValue)
                .ThenBy(entry => entry.Id, StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception error) when (IsOptionalContentFailure(error))
        {
            return null;
        }
    }

    public static string? ReadVersionOptional(string packageRoot)
    {
        try
        {
            return ReadManifest(packageRoot)?.Version;
        }
        catch (Exception error) when (IsOptionalContentFailure(error))
        {
            return null;
        }
    }

    private static KnowledgeManifest? ReadManifest(string packageRoot)
    {
        if (string.IsNullOrWhiteSpace(packageRoot) || !Path.IsPathFullyQualified(packageRoot))
        {
            throw new ArgumentException("Package root must be an absolute path.", nameof(packageRoot));
        }

        var knowledgeRoot = Path.Combine(packageRoot, "knowledge");
        var manifestPath = Path.Combine(knowledgeRoot, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        using var document = ParseJson(File.ReadAllBytes(manifestPath));
        var root = RequireObject(document.RootElement);
        if (!root.TryGetProperty("schema_version", out var schema)
            || schema.ValueKind != JsonValueKind.Number
            || schema.GetInt32() != SchemaVersion)
        {
            throw new FormatException("Unsupported knowledge schema_version.");
        }

        var version = RequireSafeId(root, "knowledge_version", 64);
        var files = new List<KnowledgeFile> { ReadFileDescriptor(root, "topics") };
        if (root.TryGetProperty("appearance_overrides", out var overrides))
        {
            if (overrides.ValueKind != JsonValueKind.Null)
            {
                files.Add(ReadFileDescriptorValue(overrides, "appearance_overrides"));
            }
        }

        RejectUnknown(root, "schema_version", "knowledge_version", "topics", "appearance_overrides");
        return new KnowledgeManifest(version, files);
    }

    private static KnowledgeFile ReadFileDescriptor(JsonElement root, string name)
    {
        var descriptor = root.TryGetProperty(name, out var value)
            ? RequireObject(value)
            : throw new FormatException($"Missing required property '{name}'.");
        return ReadFileDescriptorValue(descriptor, name);
    }

    private static KnowledgeFile ReadFileDescriptorValue(JsonElement descriptor, string name)
    {
        descriptor = RequireObject(descriptor);
        RejectUnknown(descriptor, "path", "sha256");
        var path = RequireString(descriptor, "path");
        ValidateRelativePath(path);
        var hash = RequireSha256(descriptor, "sha256");
        return new KnowledgeFile(path, hash);
    }

    private static void ReadEntries(
        string packageRoot,
        KnowledgeFile file,
        string servantId,
        string appearanceId,
        ICollection<KnowledgeEntry> destination)
    {
        var path = ResolveRelativePath(Path.Combine(packageRoot, "knowledge"), file.Path);
        var bytes = File.ReadAllBytes(path);
        VerifyHash(bytes, file.Hash);
        var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        using var reader = new StringReader(text);
        var lineNumber = 0;
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (line.Length > 32_000)
            {
                throw new FormatException($"Knowledge line {lineNumber} is too long.");
            }

            using var document = ParseJson(Encoding.UTF8.GetBytes(line));
            var entry = ReadEntry(document.RootElement);
            if (!string.Equals(entry.ServantId, servantId, StringComparison.Ordinal)
                || !entry.IsApproved
                || (entry.AppearanceId is not null && !string.Equals(entry.AppearanceId, appearanceId, StringComparison.Ordinal)))
            {
                continue;
            }

            destination.Add(entry);
        }
    }

    private static KnowledgeEntry ReadEntry(JsonElement element)
    {
        var root = RequireObject(element);
        var id = RequireSafeId(root, "id");
        var servantId = RequireSafeId(root, "servant_id");
        var topic = RequireBoundedText(root, "topic", 256);
        var summary = RequireBoundedText(root, "summary", 4_000);
        var approval = RequireString(root, "approval");
        if (approval is not ("approved" or "pending" or "rejected"))
        {
            throw new FormatException("Knowledge approval must be approved, pending, or rejected.");
        }

        var kind = KnowledgeKind.Profile;
        if (root.TryGetProperty("kind", out var kindElement))
        {
            var kindText = RequireString(root, "kind");
            kind = kindText switch
            {
                "profile" => KnowledgeKind.Profile,
                "story" => KnowledgeKind.Story,
                _ => throw new FormatException($"Unknown knowledge kind '{kindText}'."),
            };
        }

        var appearanceId = ReadOptionalId(root, "appearance_id");
        var sourceLocator = ReadOptionalText(root, "source_locator", 512);
        int? rank = null;
        if (root.TryGetProperty("rank", out var rankElement) && rankElement.ValueKind != JsonValueKind.Null)
        {
            rank = rankElement.ValueKind == JsonValueKind.Number ? rankElement.GetInt32() : throw new FormatException("Knowledge rank must be an integer.");
            if (rank < 0)
            {
                throw new FormatException("Knowledge rank cannot be negative.");
            }
        }

        RejectUnknown(root, "id", "servant_id", "topic", "summary", "approval", "kind", "appearance_id", "source_locator", "rank");
        return new KnowledgeEntry(id, servantId, topic, summary, approval, kind, appearanceId, sourceLocator, rank);
    }

    private static JsonDocument ParseJson(byte[] bytes) =>
        JsonDocument.Parse(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes), JsonOptions);

    private static string ResolveRelativePath(string root, string relativePath)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : throw new FormatException("Knowledge content path escapes the package root.");
    }

    private static void VerifyHash(byte[] bytes, string expectedHash)
    {
        var actual = Convert.ToHexString(SHA256.HashData(bytes));
        if (!actual.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException("Knowledge content hash does not match the manifest.");
        }
    }

    private static JsonElement RequireObject(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object ? element : throw new FormatException("Expected a JSON object.");

    private static string RequireString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new FormatException($"Property '{name}' must be a string.");

    private static string RequireSafeId(JsonElement element, string name, int maxLength = 128) =>
        RequireSafeId(RequireString(element, name), name, maxLength);

    private static string RequireSafeId(string value, string name, int maxLength)
    {
        if (value.Length == 0 || value.Length > maxLength || !IsSafeId(value))
        {
            throw new FormatException($"Property '{name}' is not a safe identifier.");
        }

        return value;
    }

    private static string RequireBoundedText(JsonElement element, string name, int maxLength)
    {
        var value = RequireString(element, name).Trim();
        return value.Length == 0 || value.Length > maxLength ? throw new FormatException($"Property '{name}' is too long or empty.") : value;
    }

    private static bool IsSafeId(string value) =>
        value.Length > 0 && IsAsciiAlphaNumeric(value[0]) && value.All(character => IsAsciiAlphaNumeric(character) || character is '.' or '_' or '-');

    private static bool IsAsciiAlphaNumeric(char value) =>
        value is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';

    private static string? ReadOptionalId(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String
            ? RequireSafeId(value.GetString()!, name, 128)
            : throw new FormatException($"Property '{name}' must be a string or null.");
    }

    private static string? ReadOptionalText(JsonElement element, string name, int maxLength)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new FormatException($"Property '{name}' must be a string or null.");
        }

        var text = value.GetString()!.Trim();
        return text.Length == 0 || text.Length > maxLength ? throw new FormatException($"Property '{name}' is too long.") : text;
    }

    private static string RequireSha256(JsonElement element, string name)
    {
        var hash = RequireString(element, name);
        if (hash.Length != 64)
        {
            throw new FormatException($"Property '{name}' must be a SHA-256 hash.");
        }

        try
        {
            _ = Convert.FromHexString(hash);
        }
        catch (FormatException error)
        {
            throw new FormatException($"Property '{name}' must be a SHA-256 hash.", error);
        }

        return hash;
    }

    private static void ValidateRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains(':') || path.Contains('\0')
            || path.Split('/', '\\').Any(part => part is "" or "." or ".."))
        {
            throw new FormatException("Knowledge content paths must stay under knowledge/.");
        }
    }

    private static void RejectUnknown(JsonElement element, params string[] known)
    {
        var knownSet = known.ToHashSet(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!knownSet.Contains(property.Name))
            {
                throw new FormatException($"Unknown property '{property.Name}'.");
            }
        }
    }

    private static bool IsOptionalContentFailure(Exception error) =>
        error is ArgumentException or FormatException or JsonException or IOException or UnauthorizedAccessException
            or InvalidOperationException or OverflowException;

    private sealed record KnowledgeManifest(string Version, IReadOnlyList<KnowledgeFile> Files);
    private sealed record KnowledgeFile(string Path, string Hash);
}
