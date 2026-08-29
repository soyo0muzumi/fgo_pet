using System.Text.Json;
using System.Text.RegularExpressions;
using FgoPet.Core.Portraits;

namespace FgoPet.App.Dialogue;

public sealed record MemoryCandidateDraft(string Text);

public sealed record ValidatedChatOutput(
    string Text,
    ExpressionSemantic Expression,
    string? FeedbackType,
    MemoryCandidateDraft? MemoryCandidate);

/// <summary>Accepts a small, bounded model envelope and ignores unsupported presentation values.</summary>
public static class StructuredOutputValidator
{
    private const int MaxTextLength = 12_000;
    private const int MaxMemoryLength = 2_000;
    private static readonly Regex TextFallback = new(
        "\\\"text\\\"\\s*:\\s*\\\"(?<text>(?:\\\\.|[^\\\"\\\\])*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static ValidatedChatOutput Validate(string responseText, IReadOnlySet<string> supportedExpressions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(responseText);
        ArgumentNullException.ThrowIfNull(supportedExpressions);
        var raw = responseText.Trim();
        if (raw.StartsWith('{'))
        {
            try
            {
                using var document = JsonDocument.Parse(raw, new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                });
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    return ReadObject(document.RootElement, raw, supportedExpressions);
                }
            }
            catch (JsonException)
            {
                // A partially streamed JSON envelope may still contain a safe text field.
                return new ValidatedChatOutput(ExtractFallbackText(raw), ExpressionSemantic.Neutral, null, null);
            }
        }

        return new ValidatedChatOutput(Bound(raw, MaxTextLength), ExpressionSemantic.Neutral, null, null);
    }

    private static ValidatedChatOutput ReadObject(
        JsonElement root,
        string raw,
        IReadOnlySet<string> supportedExpressions)
    {
        var text = ReadString(root, "text") ?? ExtractFallbackText(raw);
        text = Bound(text, MaxTextLength);
        var emotion = ReadString(root, "emotion");
        var expression = emotion is not null
            && supportedExpressions.Contains(emotion)
            && ExpressionSemanticKeys.TryParseKey(emotion, out var parsed)
            ? parsed
            : ExpressionSemantic.Neutral;
        var feedbackType = ReadSafeId(root, "feedback_type", 64);
        var memoryCandidate = ReadMemoryCandidate(root);
        return new ValidatedChatOutput(text, expression, feedbackType, memoryCandidate);
    }

    private static MemoryCandidateDraft? ReadMemoryCandidate(JsonElement root)
    {
        if (!root.TryGetProperty("memory_candidate", out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Object => ReadString(value, "text"),
            _ => null,
        };
        return string.IsNullOrWhiteSpace(text) ? null : new MemoryCandidateDraft(Bound(text.Trim(), MaxMemoryLength));
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? ReadSafeId(JsonElement root, string name, int maxLength)
    {
        var value = ReadString(root, name)?.Trim();
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength
            || !value.All(character => char.IsLetterOrDigit(character) || character is '.' or '_' or '-'))
        {
            return null;
        }

        return value;
    }

    private static string ExtractFallbackText(string raw)
    {
        var match = TextFallback.Match(raw);
        if (match.Success)
        {
            try
            {
                var jsonString = $"\"{match.Groups["text"].Value}\"";
                var value = JsonSerializer.Deserialize<string>(jsonString);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return Bound(value.Trim(), MaxTextLength);
                }
            }
            catch (JsonException)
            {
                // Fall through to the bounded raw response.
            }
        }

        return Bound(raw, MaxTextLength);
    }

    private static string Bound(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..(maxLength - 1)] + "…";
}
