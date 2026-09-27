using System.Text.Json;
using System.Text.Json.Serialization;

namespace FgoPet.Core.Packs;

public static class PackJson
{
    // Strict reflection-based options. Unknown members are captured by each DTO's
    // [JsonExtensionData] slot and rejected here; enums are snake_case text only.
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = false,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false),
        },
    };

    /// <summary>
    /// Deserializes a pack/appearance manifest strictly: unknown members, missing
    /// required members, or structural invariant violations throw
    /// <see cref="PackFailureException"/> bearing a stable <see cref="PackFailure"/>.
    /// </summary>
    public static T DeserializeStrict<T>(string json, string? relativePath = null) where T : class, IStrictDeserializable
    {
        T value;
        try
        {
            value = JsonSerializer.Deserialize<T>(json, Options)
                ?? throw new JsonException("JSON 内容为空。");
            if (value.ExtraData is { Count: > 0 })
            {
                throw new JsonException($"存在未知属性: {string.Join(", ", value.ExtraData.Keys)}");
            }
        }
        catch (JsonException error)
        {
            throw new PackFailureException(new PackFailure(PackErrorCode.ManifestMalformed, error.Message, relativePath));
        }

        value.ValidateStructural();
        return value;
    }
}