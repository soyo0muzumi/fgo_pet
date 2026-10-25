using System.Text.Json;

namespace FgoPet.Kernel.Agent;

/// <summary>A bounded JSON Schema subset. Unsupported assertion keywords fail closed.</summary>
public static class ToolArgumentsValidator
{
    private const int MaxDepth = 32;
    private static readonly HashSet<string> Types = ["object", "array", "string", "number", "integer", "boolean", "null"];
    private static readonly HashSet<string> Keywords =
    [
        "type", "properties", "required", "additionalProperties", "items", "enum", "const",
        "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "minLength", "maxLength",
        "minItems", "maxItems", "minProperties", "maxProperties", "title", "description", "default", "examples", "$schema"
    ];

    public static bool Validate(JsonElement schema, JsonElement arguments, out string? errorCode)
    {
        errorCode = null;
        try
        {
            if (!IsSupported(schema, 0)) { errorCode = "TOOL_UNSUPPORTED_SCHEMA"; return false; }
            if (arguments.ValueKind != JsonValueKind.Object || !Matches(schema, arguments, 0))
            { errorCode = "TOOL_INVALID_ARGUMENTS"; return false; }
            return true;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ObjectDisposedException)
        { errorCode = "TOOL_UNSUPPORTED_SCHEMA"; return false; }
    }

    private static bool IsSupported(JsonElement schema, int depth)
    {
        if (depth > MaxDepth || schema.ValueKind != JsonValueKind.Object || HasDuplicateProperties(schema)) return false;
        foreach (var property in schema.EnumerateObject())
        {
            if (!Keywords.Contains(property.Name)) return false;
            var value = property.Value;
            switch (property.Name)
            {
                case "type":
                    if (value.ValueKind == JsonValueKind.String)
                    { if (!Types.Contains(value.GetString()!)) return false; }
                    else if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0
                        || value.EnumerateArray().Any(type => type.ValueKind != JsonValueKind.String || !Types.Contains(type.GetString()!))) return false;
                    break;
                case "properties":
                    if (value.ValueKind != JsonValueKind.Object || HasDuplicateProperties(value)
                        || value.EnumerateObject().Any(child => !IsSupported(child.Value, depth + 1))) return false;
                    break;
                case "items":
                    if (!IsSupported(value, depth + 1)) return false;
                    break;
                case "additionalProperties":
                    if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                        && !IsSupported(value, depth + 1)) return false;
                    break;
                case "required":
                    if (value.ValueKind != JsonValueKind.Array || value.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String)
                        || value.EnumerateArray().Select(item => item.GetString()).Distinct(StringComparer.Ordinal).Count() != value.GetArrayLength()) return false;
                    break;
                case "enum":
                    if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0) return false;
                    break;
                case "minimum": case "maximum": case "exclusiveMinimum": case "exclusiveMaximum":
                    if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out _)) return false;
                    break;
                case "minLength": case "maxLength": case "minItems": case "maxItems": case "minProperties": case "maxProperties":
                    if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var bound) || bound < 0) return false;
                    break;
                case "title": case "description": case "$schema":
                    if (value.ValueKind != JsonValueKind.String) return false;
                    break;
                case "examples":
                    if (value.ValueKind != JsonValueKind.Array) return false;
                    break;
            }
        }
        return true;
    }

    private static bool Matches(JsonElement schema, JsonElement value, int depth)
    {
        if (depth > MaxDepth) return false;
        if (schema.TryGetProperty("type", out var type)
            && !(type.ValueKind == JsonValueKind.String ? HasType(value, type.GetString()!)
                : type.EnumerateArray().Any(item => HasType(value, item.GetString()!)))) return false;
        if (schema.TryGetProperty("enum", out var enumeration) && !enumeration.EnumerateArray().Any(item => Equivalent(item, value))) return false;
        if (schema.TryGetProperty("const", out var constant) && !Equivalent(constant, value)) return false;

        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                if (HasDuplicateProperties(value)) return false;
                if (!LengthMatches(schema, value.EnumerateObject().Count(), "minProperties", "maxProperties")) return false;
                if (schema.TryGetProperty("required", out var required)
                    && required.EnumerateArray().Any(item => !value.TryGetProperty(item.GetString()!, out _))) return false;
                var hasProperties = schema.TryGetProperty("properties", out var properties);
                foreach (var property in value.EnumerateObject())
                {
                    if (hasProperties && properties.TryGetProperty(property.Name, out var childSchema))
                    { if (!Matches(childSchema, property.Value, depth + 1)) return false; }
                    else if (schema.TryGetProperty("additionalProperties", out var additional))
                    {
                        if (additional.ValueKind == JsonValueKind.False) return false;
                        if (additional.ValueKind == JsonValueKind.Object && !Matches(additional, property.Value, depth + 1)) return false;
                    }
                    if (!HasUniqueNestedProperties(property.Value, depth + 1)) return false;
                }
                break;
            case JsonValueKind.Array:
                if (!LengthMatches(schema, value.GetArrayLength(), "minItems", "maxItems")) return false;
                foreach (var item in value.EnumerateArray())
                {
                    if (schema.TryGetProperty("items", out var items) && !Matches(items, item, depth + 1)) return false;
                    if (!HasUniqueNestedProperties(item, depth + 1)) return false;
                }
                break;
            case JsonValueKind.String:
                if (!LengthMatches(schema, value.GetString()!.EnumerateRunes().Count(), "minLength", "maxLength")) return false;
                break;
            case JsonValueKind.Number:
                if (!value.TryGetDecimal(out var number)) return false;
                if (schema.TryGetProperty("minimum", out var minimum) && number < minimum.GetDecimal()) return false;
                if (schema.TryGetProperty("maximum", out var maximum) && number > maximum.GetDecimal()) return false;
                if (schema.TryGetProperty("exclusiveMinimum", out var lower) && number <= lower.GetDecimal()) return false;
                if (schema.TryGetProperty("exclusiveMaximum", out var upper) && number >= upper.GetDecimal()) return false;
                break;
            case JsonValueKind.Undefined: return false;
        }
        return true;
    }

    private static bool HasType(JsonElement value, string type) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "number" => value.ValueKind == JsonValueKind.Number,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) && decimal.Truncate(number) == number,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => false
    };

    private static bool LengthMatches(JsonElement schema, int count, string min, string max)
        => (!schema.TryGetProperty(min, out var minimum) || count >= minimum.GetInt32())
            && (!schema.TryGetProperty(max, out var maximum) || count <= maximum.GetInt32());

    private static bool HasDuplicateProperties(JsonElement value)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        return value.EnumerateObject().Any(property => !names.Add(property.Name));
    }

    private static bool HasUniqueNestedProperties(JsonElement value, int depth)
    {
        if (depth > MaxDepth) return false;
        return value.ValueKind switch
        {
            JsonValueKind.Object => !HasDuplicateProperties(value)
                && value.EnumerateObject().All(property => HasUniqueNestedProperties(property.Value, depth + 1)),
            JsonValueKind.Array => value.EnumerateArray().All(item => HasUniqueNestedProperties(item, depth + 1)),
            _ => true
        };
    }

    private static bool Equivalent(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind) return false;
        return left.ValueKind switch
        {
            JsonValueKind.Number => left.TryGetDecimal(out var a) && right.TryGetDecimal(out var b) && a == b,
            JsonValueKind.String => left.GetString() == right.GetString(),
            JsonValueKind.Object => !HasDuplicateProperties(left) && !HasDuplicateProperties(right)
                && left.EnumerateObject().Count() == right.EnumerateObject().Count()
                && left.EnumerateObject().All(property => right.TryGetProperty(property.Name, out var other) && Equivalent(property.Value, other)),
            JsonValueKind.Array => left.GetArrayLength() == right.GetArrayLength()
                && left.EnumerateArray().Zip(right.EnumerateArray()).All(pair => Equivalent(pair.First, pair.Second)),
            JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => true,
            _ => false
        };
    }
}
