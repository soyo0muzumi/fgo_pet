using System.Text.Json;
using FgoPet.Core.Packs;
using FgoPet.Core.Portraits;

namespace FgoPet.Infrastructure.Packs;

/// <summary>
/// Strict, optional <c>dialogue/</c> parsing. Any structural problem yields
/// <c>null</c> (the neutral fallback), never a user-facing error: dialogue is
/// characterization only.
/// </summary>
public static class DialogueManifestReader
{
    public const int SchemaVersion = 1;
    public const int MaxTextScalars = 160;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = false,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
    };

    public static DialogueBundle? ReadOptional(string packageRoot)
    {
        try
        {
            return Read(packageRoot);
        }
        catch (Exception error) when (error is JsonException or FormatException or IOException)
        {
            return null;
        }
    }

    private static DialogueBundle Read(string packageRoot)
    {
        var dialogueRoot = Path.Combine(packageRoot, "dialogue");
        if (!Directory.Exists(dialogueRoot))
        {
            return null!;
        }

        var manifestPath = Path.Combine(dialogueRoot, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            return null!;
        }

        using var manifestDocument = JsonDocument.Parse(File.ReadAllText(manifestPath), new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
        });
        var manifest = manifestDocument.RootElement;
        RequireObject(manifest);

        var schemaVersion = RequireInt(manifest, "schema_version");
        if (schemaVersion != SchemaVersion)
        {
            throw new FormatException($"Unsupported dialogue schema_version {schemaVersion}.");
        }

        var defaultLocale = RequireString(manifest, "default_locale");
        var known = new HashSet<string>(StringComparer.Ordinal) { "schema_version", "default_locale", "localizations" };
        RejectUnknownMembers(manifest, known);

        var localizationsElement = RequireProperty(manifest, "localizations");
        RequireObject(localizationsElement);
        var localizations = new Dictionary<string, DialogueLocalization>(StringComparer.OrdinalIgnoreCase);
        foreach (var localeProperty in localizationsElement.EnumerateObject())
        {
            var relativePath = localeProperty.Value.GetString()
                ?? throw new FormatException($"Localization '{localeProperty.Name}' path is not a string.");
            ValidateRelativePath(relativePath);
            var locale = ReadLocalization(Path.Combine(dialogueRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            localizations[localeProperty.Name] = new DialogueLocalization(localeProperty.Name, locale);
        }

        if (localizations.Count == 0)
        {
            throw new FormatException("Dialogue manifest declares no localizations.");
        }

        if (!localizations.ContainsKey(defaultLocale))
        {
            throw new FormatException($"Default locale '{defaultLocale}' has no localization.");
        }

        return new DialogueBundle(defaultLocale, localizations);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<DialogueCandidate>> ReadLocalization(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
        });
        RequireObject(document.RootElement);
        var events = new Dictionary<string, IReadOnlyList<DialogueCandidate>>(StringComparer.Ordinal);
        foreach (var eventProperty in document.RootElement.EnumerateObject())
        {
            var candidates = new List<DialogueCandidate>();
            foreach (var candidateElement in eventProperty.Value.EnumerateArray())
            {
                candidates.Add(ReadCandidate(candidateElement));
            }

            events[eventProperty.Name] = candidates;
        }

        return events;
    }

    private static DialogueCandidate ReadCandidate(JsonElement element)
    {
        RequireObject(element);
        var id = RequireString(element, "id");
        if (!System.Text.RegularExpressions.Regex.IsMatch(id, @"^[a-z0-9][a-z0-9._-]{0,63}$"))
        {
            throw new FormatException($"Candidate id '{id}' is not a safe dialogue id.");
        }

        var text = RequireString(element, "text");
        if (text.Length is 0 or > MaxTextScalars || string.IsNullOrWhiteSpace(text))
        {
            throw new FormatException($"Candidate '{id}' text must be 1-{MaxTextScalars} non-whitespace characters.");
        }

        var weight = element.TryGetProperty("weight", out var weightElement) ? weightElement.GetInt32() : 100;
        if (weight is < 1 or > 100)
        {
            throw new FormatException($"Candidate '{id}' weight {weight} is outside 1-100.");
        }

        ExpressionSemantic? expression = null;
        if (element.TryGetProperty("expression", out var expressionElement))
        {
            var key = expressionElement.GetString();
            if (key is null || !ExpressionSemanticKeys.TryParseKey(key, out var semantic))
            {
                throw new FormatException($"Candidate '{id}' declares unknown expression '{key}'.");
            }

            expression = semantic;
        }

        var known = expression.HasValue
            ? new HashSet<string> { "id", "text", "weight", "expression" }
            : new HashSet<string> { "id", "text", "weight" };
        RejectUnknownMembers(element, known);

        return new DialogueCandidate(id, text, weight, expression);
    }

    private static void ValidateRelativePath(string path)
    {
        if (path.Contains("..") || Path.IsPathRooted(path)
            || path.EndsWith("..", StringComparison.Ordinal)
            || path.Split('/', '\\').Any(part => part.Length == 0 && path.Length > 0 && !path.EndsWith('/')))
        {
            throw new FormatException($"Dialogue localization path '{path}' must stay under dialogue/.");
        }
    }

    private static void RequireObject(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("Expected a JSON object.");
        }
    }

    private static JsonElement RequireProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
            ? value
            : throw new FormatException($"Missing required property '{name}'.");

    private static string RequireString(JsonElement element, string name) =>
        RequireProperty(element, name).GetString()
            ?? throw new FormatException($"Property '{name}' must be a string.");

    private static int RequireInt(JsonElement element, string name) =>
        RequireProperty(element, name).GetInt32();

    private static void RejectUnknownMembers(JsonElement element, HashSet<string> known)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!known.Contains(property.Name))
            {
                throw new FormatException($"Unknown property '{property.Name}'.");
            }
        }
    }
}
