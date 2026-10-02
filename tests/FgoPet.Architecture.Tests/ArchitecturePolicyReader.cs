using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FgoPet.Architecture.Tests;

internal enum ArchitectureLayer
{
    Contract,
    Application,
    Implementation,
    Desktop,
    Host,
    Test,
    LegacyMixed,
    ArchitectureTest,
}

internal enum ArchitectureRole
{
    Production,
    Test,
    Host,
}

internal enum ArchitectureDependencyKind
{
    Compile,
    Companion,
}

internal sealed record ArchitectureProject(
    string Path,
    string Module,
    ArchitectureLayer Layer,
    ArchitectureRole Role);

internal sealed record ArchitectureDependencyRule(
    string Id,
    ImmutableArray<string> FromModules,
    ImmutableArray<ArchitectureLayer> FromLayers,
    ImmutableArray<string> ToModules,
    ImmutableArray<ArchitectureLayer> ToLayers,
    ImmutableArray<ArchitectureDependencyKind> Kinds);

internal sealed record ArchitecturePolicy(
    int SchemaVersion,
    ImmutableArray<ArchitectureProject> Projects,
    ImmutableArray<ArchitectureDependencyRule> DependencyRules);

internal interface IArchitecturePolicyReader
{
    ArchitecturePolicy Read(string json, string sourceName);
}

internal sealed class ArchitecturePolicyException : Exception
{
    public ArchitecturePolicyException(string message) : base(message)
    {
    }
}

internal sealed class StrictJsonArchitecturePolicyReader : IArchitecturePolicyReader
{
    private static readonly Regex IdentifierPattern = new("\\A[a-z0-9]+(?:-[a-z0-9]+)*\\z", RegexOptions.CultureInvariant);
    private static readonly IReadOnlyDictionary<string, ArchitectureLayer> Layers =
        new Dictionary<string, ArchitectureLayer>(StringComparer.Ordinal)
        {
            ["contract"] = ArchitectureLayer.Contract,
            ["application"] = ArchitectureLayer.Application,
            ["implementation"] = ArchitectureLayer.Implementation,
            ["desktop"] = ArchitectureLayer.Desktop,
            ["host"] = ArchitectureLayer.Host,
            ["test"] = ArchitectureLayer.Test,
            ["legacy-mixed"] = ArchitectureLayer.LegacyMixed,
            ["architecture-test"] = ArchitectureLayer.ArchitectureTest,
        };
    private static readonly IReadOnlyDictionary<string, ArchitectureRole> Roles =
        new Dictionary<string, ArchitectureRole>(StringComparer.Ordinal)
        {
            ["production"] = ArchitectureRole.Production,
            ["test"] = ArchitectureRole.Test,
            ["host"] = ArchitectureRole.Host,
        };

    public ArchitecturePolicy Read(string json, string sourceName)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);

        try
        {
            using var document = JsonDocument.Parse(json);
            return ParseRoot(document.RootElement, sourceName);
        }
        catch (JsonException error)
        {
            var location = error.LineNumber is null
                ? "JSON"
                : $"line {error.LineNumber.Value + 1}, byte {error.BytePositionInLine.GetValueOrDefault() + 1}";
            throw Invalid(sourceName, location, "malformed JSON");
        }
    }

    private static ArchitecturePolicy ParseRoot(JsonElement root, string source)
    {
        var properties = ObjectProperties(root, source, "$");
        EnsureAllowed(properties, source, "$.", "schemaVersion", "projects", "dependencyRules");
        var schemaVersion = RequiredInt(properties, "schemaVersion", source, "$.schemaVersion");
        if (schemaVersion != 1)
        {
            throw Invalid(source, "$.schemaVersion", "must be 1");
        }

        var projects = RequiredArray(properties, "projects", source, "$.projects")
            .EnumerateArray()
            .Select((item, index) => ParseProject(item, source, $"$.projects[{index}]"))
            .ToImmutableArray();
        var projectPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in projects)
        {
            if (!projectPaths.Add(project.Path))
            {
                throw Invalid(source, "$.projects", "contains duplicate project paths");
            }
        }

        var rules = RequiredArray(properties, "dependencyRules", source, "$.dependencyRules")
            .EnumerateArray()
            .Select((item, index) => ParseRule(item, source, $"$.dependencyRules[{index}]"))
            .ToImmutableArray();
        var ruleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in rules)
        {
            if (!ruleIds.Add(rule.Id))
            {
                throw Invalid(source, "$.dependencyRules", "contains duplicate rule IDs");
            }
        }

        return new ArchitecturePolicy(schemaVersion, projects, rules);
    }

    private static ArchitectureProject ParseProject(JsonElement element, string source, string location)
    {
        var properties = ObjectProperties(element, source, location);
        EnsureAllowed(properties, source, location, "path", "module", "layer", "role");
        var path = RequiredString(properties, "path", source, $"{location}.path");
        ValidatePath(path, source, $"{location}.path");
        var module = RequiredString(properties, "module", source, $"{location}.module");
        ValidateIdentifier(module, source, $"{location}.module");
        var layer = ParseLayer(RequiredString(properties, "layer", source, $"{location}.layer"), source, $"{location}.layer");
        var role = ParseRole(RequiredString(properties, "role", source, $"{location}.role"), source, $"{location}.role");
        return new ArchitectureProject(path, module, layer, role);
    }

    private static ArchitectureDependencyRule ParseRule(JsonElement element, string source, string location)
    {
        var properties = ObjectProperties(element, source, location);
        EnsureAllowed(properties, source, location, "id", "fromModules", "fromLayers", "toModules", "toLayers", "kinds");
        var id = RequiredString(properties, "id", source, $"{location}.id");
        ValidateIdentifier(id, source, $"{location}.id");
        var fromModules = ParseModules(RequiredArray(properties, "fromModules", source, $"{location}.fromModules"), source, $"{location}.fromModules");
        var fromLayers = ParseLayers(RequiredArray(properties, "fromLayers", source, $"{location}.fromLayers"), source, $"{location}.fromLayers");
        var toModules = ParseModules(RequiredArray(properties, "toModules", source, $"{location}.toModules"), source, $"{location}.toModules");
        var toLayers = ParseLayers(RequiredArray(properties, "toLayers", source, $"{location}.toLayers"), source, $"{location}.toLayers");
        var kinds = ParseKinds(RequiredArray(properties, "kinds", source, $"{location}.kinds"), source, $"{location}.kinds");
        return new ArchitectureDependencyRule(id, fromModules, fromLayers, toModules, toLayers, kinds);
    }

    private static Dictionary<string, JsonElement> ObjectProperties(JsonElement element, string source, string location)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Invalid(source, location, "must be an object");
        }

        var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!properties.TryAdd(property.Name, property.Value))
            {
                throw Invalid(source, $"{location}.{property.Name}", "duplicate property");
            }
        }

        return properties;
    }

    private static void EnsureAllowed(Dictionary<string, JsonElement> properties, string source, string location, params string[] allowed)
    {
        var allowedSet = new HashSet<string>(allowed, StringComparer.Ordinal);
        foreach (var property in properties.Keys)
        {
            if (!allowedSet.Contains(property))
            {
                throw Invalid(source, $"{location}{property}", "unknown property");
            }
        }
    }

    private static int RequiredInt(Dictionary<string, JsonElement> properties, string name, string source, string location)
    {
        var value = Required(properties, name, source, location);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result))
        {
            throw Invalid(source, location, "must be an integer");
        }

        return result;
    }

    private static string RequiredString(Dictionary<string, JsonElement> properties, string name, string source, string location)
    {
        var value = Required(properties, name, source, location);
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw Invalid(source, location, "must be a non-empty string");
        }

        return value.GetString()!;
    }

    private static JsonElement RequiredArray(Dictionary<string, JsonElement> properties, string name, string source, string location)
    {
        var value = Required(properties, name, source, location);
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0)
        {
            throw Invalid(source, location, "must be a non-empty array");
        }

        return value;
    }

    private static JsonElement Required(Dictionary<string, JsonElement> properties, string name, string source, string location)
    {
        if (!properties.TryGetValue(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            throw Invalid(source, location, "is required and may not be null");
        }

        return value;
    }

    private static ImmutableArray<string> ParseModules(JsonElement array, string source, string location)
    {
        var values = array.EnumerateArray().Select((item, index) =>
        {
            var value = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
            if (string.IsNullOrWhiteSpace(value))
            {
                throw Invalid(source, $"{location}[{index}]", "must be a non-empty module identifier");
            }
            ValidateIdentifier(value!, source, $"{location}[{index}]");
            return value!;
        }).ToImmutableArray();
        EnsureUnique(values, source, location);
        return values;
    }

    private static ImmutableArray<ArchitectureLayer> ParseLayers(JsonElement array, string source, string location)
    {
        var values = array.EnumerateArray().Select((item, index) => ParseLayer(
            item.ValueKind == JsonValueKind.String ? item.GetString() ?? string.Empty : string.Empty,
            source,
            $"{location}[{index}]")).ToImmutableArray();
        EnsureUnique(values, source, location);
        return values;
    }

    private static ImmutableArray<ArchitectureDependencyKind> ParseKinds(JsonElement array, string source, string location)
    {
        var values = array.EnumerateArray().Select((item, index) =>
        {
            var value = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
            return value switch
            {
                "compile" => ArchitectureDependencyKind.Compile,
                "companion" => ArchitectureDependencyKind.Companion,
                _ => throw Invalid(source, $"{location}[{index}]", "must be compile or companion"),
            };
        }).ToImmutableArray();
        EnsureUnique(values, source, location);
        return values;
    }

    private static ArchitectureLayer ParseLayer(string value, string source, string location) =>
        Layers.TryGetValue(value, out var result)
            ? result
            : throw Invalid(source, location, "has an invalid layer");

    private static ArchitectureRole ParseRole(string value, string source, string location) =>
        Roles.TryGetValue(value, out var result)
            ? result
            : throw Invalid(source, location, "has an invalid role");

    private static void ValidateIdentifier(string value, string source, string location)
    {
        if (!IdentifierPattern.IsMatch(value))
        {
            throw Invalid(source, location, "must be lowercase kebab-case");
        }
    }

    private static void ValidatePath(string path, string source, string location)
    {
        if (path.StartsWith('/') || path.EndsWith('/') || path.Contains('\\') || path.Contains("//", StringComparison.Ordinal) ||
            Path.IsPathRooted(path) || Regex.IsMatch(path, "^[A-Za-z]:", RegexOptions.CultureInvariant) ||
            !path.EndsWith(".csproj", StringComparison.Ordinal) ||
            path.Split('/').Any(segment => segment is "." or ".."))
        {
            throw Invalid(source, location, "must be a normalized repository-relative .csproj path");
        }
    }

    private static void EnsureUnique<T>(IReadOnlyList<T> values, string source, string location)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            var key = value?.ToString() ?? string.Empty;
            if (!seen.Add(key))
            {
                throw Invalid(source, location, "contains duplicate entries");
            }
        }
    }

    private static ArchitecturePolicyException Invalid(string source, string location, string detail) =>
        new($"Invalid architecture policy '{source}' at {location}: {detail}.");
}
