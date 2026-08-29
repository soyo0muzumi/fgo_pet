using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FgoPet.Core.Packs;

namespace FgoPet.Infrastructure.Packs;

/// <summary>
/// Reads the optional, declaration-only Persona layer. Any malformed optional
/// content returns null so legacy art packs remain loadable.
/// </summary>
public static class PersonaManifestReader
{
    public const int SchemaVersion = 1;

    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
    };

    public static PersonaBundle? ReadOptional(string packageRoot, string servantId)
    {
        try
        {
            return Read(packageRoot, servantId);
        }
        catch (Exception error) when (IsOptionalContentFailure(error))
        {
            return null;
        }
    }

    private static PersonaBundle? Read(string packageRoot, string servantId)
    {
        RequireAbsoluteRoot(packageRoot);
        var personaRoot = Path.Combine(packageRoot, "persona");
        var manifestPath = Path.Combine(personaRoot, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        using var manifest = ParseJson(File.ReadAllBytes(manifestPath));
        var root = RequireObject(manifest.RootElement);
        RequireSchema(root, "persona");
        var declaredServantId = RequireSafeId(root, "servant_id");
        if (!string.Equals(declaredServantId, servantId, StringComparison.Ordinal))
        {
            throw new FormatException("Persona servant_id does not match the requested servant.");
        }

        var packageId = RequireSafeId(root, "package_id");
        var packageVersion = RequireSafeId(root, "package_version", 64);
        var personaVersion = RequireSafeId(root, "persona_version", 64);
        var coreDescriptor = RequireObjectProperty(root, "core");
        RejectUnknown(root, "schema_version", "servant_id", "package_id", "package_version", "persona_version", "core", "appearances");

        var core = ReadCore(
            personaRoot,
            RequireRelativePath(coreDescriptor, "path"),
            RequireSha256(coreDescriptor, "sha256"),
            declaredServantId);

        var overlays = new List<PersonaAppearanceOverlay>();
        var appearances = root.TryGetProperty("appearances", out var appearancesElement)
            ? RequireObject(appearancesElement)
            : throw new FormatException("Missing required property 'appearances'.");
        foreach (var property in appearances.EnumerateObject())
        {
            var appearanceId = RequireSafeId(property.Name);
            var descriptor = RequireObject(property.Value);
            RejectUnknown(descriptor, "path", "sha256");
            var path = RequireRelativePath(descriptor, "path");
            var hash = RequireSha256(descriptor, "sha256");
            overlays.Add(ReadAppearance(personaRoot, path, hash, appearanceId));
        }

        return new PersonaBundle(
            declaredServantId,
            packageId,
            packageVersion,
            personaVersion,
            core.Text,
            overlays,
            core.DefaultAddress);
    }

    private static PersonaFile ReadCore(string personaRoot, string relativePath, string expectedHash, string servantId)
    {
        using var document = ReadVerifiedJson(personaRoot, relativePath, expectedHash);
        var root = RequireObject(document.RootElement);
        RequireSchema(root, "persona core");
        var declaredServantId = RequireSafeId(root, "servant_id");
        if (!string.Equals(declaredServantId, servantId, StringComparison.Ordinal))
        {
            throw new FormatException("Persona core servant_id does not match the manifest.");
        }

        RejectUnknown(root, "schema_version", "servant_id", "text", "default_address");
        return new PersonaFile(
            RequireBoundedText(root, "text", 16_000),
            ReadOptionalText(root, "default_address", 128));
    }

    private static PersonaAppearanceOverlay ReadAppearance(
        string personaRoot,
        string relativePath,
        string expectedHash,
        string appearanceId)
    {
        using var document = ReadVerifiedJson(personaRoot, relativePath, expectedHash);
        var root = RequireObject(document.RootElement);
        RequireSchema(root, "persona appearance");
        var declaredAppearanceId = RequireSafeId(root, "appearance_id");
        if (!string.Equals(declaredAppearanceId, appearanceId, StringComparison.Ordinal))
        {
            throw new FormatException("Persona appearance_id does not match the manifest key.");
        }

        RejectUnknown(root, "schema_version", "appearance_id", "text", "default_address");
        return new PersonaAppearanceOverlay(
            declaredAppearanceId,
            RequireBoundedText(root, "text", 8_000),
            ReadOptionalText(root, "default_address", 128));
    }

    private static JsonDocument ParseJson(byte[] bytes) =>
        JsonDocument.Parse(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes), JsonOptions);

    private static JsonDocument ReadVerifiedJson(string root, string relativePath, string expectedHash)
    {
        var path = ResolveRelativePath(root, relativePath);
        var bytes = File.ReadAllBytes(path);
        VerifyHash(bytes, expectedHash);
        return ParseJson(bytes);
    }

    private static string ResolveRelativePath(string root, string relativePath)
    {
        ValidateRelativePath(relativePath);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException("Persona content path escapes the package root.");
        }

        return fullPath;
    }

    private static void VerifyHash(byte[] bytes, string expectedHash)
    {
        var actual = Convert.ToHexString(SHA256.HashData(bytes));
        if (!actual.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException("Persona content hash does not match the manifest.");
        }
    }

    private static JsonElement RequireObject(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object
            ? element
            : throw new FormatException("Expected a JSON object.");

    private static JsonElement RequireObjectProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
            ? RequireObject(value)
            : throw new FormatException($"Missing required property '{name}'.");

    private static void RequireSchema(JsonElement element, string label)
    {
        if (!element.TryGetProperty("schema_version", out var schema) || schema.ValueKind != JsonValueKind.Number || schema.GetInt32() != SchemaVersion)
        {
            throw new FormatException($"Unsupported {label} schema_version.");
        }
    }

    private static string RequireSafeId(JsonElement element, string name, int maxLength = 128) =>
        RequireSafeId(RequireString(element, name), name, maxLength);

    private static string RequireSafeId(string value, string name = "id", int maxLength = 128)
    {
        if (value.Length == 0 || value.Length > maxLength || !IsSafeId(value))
        {
            throw new FormatException($"Property '{name}' is not a safe identifier.");
        }

        return value;
    }

    private static bool IsSafeId(string value) =>
        value.Length > 0 && IsAsciiAlphaNumeric(value[0]) && value.All(IsSafeIdCharacter);

    private static bool IsSafeIdCharacter(char value) =>
        IsAsciiAlphaNumeric(value) || value is '.' or '_' or '-';

    private static bool IsAsciiAlphaNumeric(char value) =>
        value is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';

    private static string RequireString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new FormatException($"Property '{name}' must be a string.");

    private static string RequireBoundedText(JsonElement element, string name, int maxLength)
    {
        var value = RequireString(element, name).Trim();
        if (value.Length == 0 || value.Length > maxLength || string.IsNullOrWhiteSpace(value))
        {
            throw new FormatException($"Property '{name}' must contain 1-{maxLength} characters.");
        }

        return value;
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

    private static string RequireRelativePath(JsonElement element, string name)
    {
        var path = RequireString(element, name);
        ValidateRelativePath(path);
        return path;
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
            throw new FormatException("Content paths must be non-rooted and stay under the package directory.");
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

    private static void RequireAbsoluteRoot(string packageRoot)
    {
        if (string.IsNullOrWhiteSpace(packageRoot) || !Path.IsPathFullyQualified(packageRoot))
        {
            throw new ArgumentException("Package root must be an absolute path.", nameof(packageRoot));
        }
    }

    private static bool IsOptionalContentFailure(Exception error) =>
        error is ArgumentException or FormatException or JsonException or IOException or UnauthorizedAccessException
            or InvalidOperationException or OverflowException;

    private sealed record PersonaFile(string Text, string? DefaultAddress);
}
