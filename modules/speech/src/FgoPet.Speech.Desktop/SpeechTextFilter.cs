using System.Text.RegularExpressions;
using FgoPet.Core.Speech;

namespace FgoPet.App.Speech;

public static partial class SpeechTextFilter
{
    public const int AutoReadDefaultLimit = 300;

    public static string Filter(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        return Whitespace().Replace(Clean(text), " ").Trim();
    }

    /// <summary>Filters a completed assistant body and splits it without dropping text.</summary>
    public static IReadOnlyList<string> SplitForSynthesis(
        string? text,
        int maxCharacters = SpeechSynthesisRequest.MaxTextLength)
    {
        if (maxCharacters <= 0) throw new ArgumentOutOfRangeException(nameof(maxCharacters));
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();

        var result = new List<string>();
        foreach (var paragraph in Clean(text).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var remaining = Whitespace().Replace(paragraph, " ").Trim();
            while (remaining.Length > 0)
            {
                if (remaining.Length <= maxCharacters)
                {
                    result.Add(remaining);
                    break;
                }

                var cut = FindCut(remaining, maxCharacters);
                result.Add(remaining[..cut].Trim());
                remaining = remaining[cut..].Trim();
            }
        }

        return result;
    }

    /// <summary>Returns only text that fits the explicit first-version auto-reading budget.</summary>
    public static bool TryGetAutoReadText(
        string? text,
        int limit,
        out string autoReadText)
    {
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit));
        var filtered = Filter(text);
        if (filtered.Length == 0 || filtered.Length > limit)
        {
            autoReadText = string.Empty;
            return false;
        }

        autoReadText = filtered;
        return true;
    }

    /// <summary>Applies the explicit first-version auto-reading budget without truncating.</summary>
    public static string ForAutoRead(string? text, int limit = AutoReadDefaultLimit) =>
        TryGetAutoReadText(text, limit, out var autoReadText) ? autoReadText : string.Empty;

    private static int FindCut(string text, int maxCharacters)
    {
        var lowerBound = Math.Max(1, maxCharacters / 2);
        for (var index = maxCharacters; index >= lowerBound; index--)
        {
            if (text[index - 1] is '。' or '！' or '？' or '；' or '.' or '!' or '?' or ';')
            {
                return index;
            }
        }

        for (var index = maxCharacters; index >= lowerBound; index--)
        {
            if (char.IsWhiteSpace(text[index - 1])) return index;
        }

        return maxCharacters;
    }

    private static string Clean(string text)
    {
        var filtered = CodeBlock().Replace(text, " ");
        filtered = TechnicalLine().Replace(filtered, " ");
        return Url().Replace(filtered, "链接");
    }

    [GeneratedRegex(@"\x60{3}[\s\S]*?\x60{3}", RegexOptions.IgnoreCase)]
    private static partial Regex CodeBlock();

    [GeneratedRegex(@"(?im)^\s*(?:tool_call|function_call|dispatch_request_id|task_id|source_instance|provider_error|http_status|reasoning|思考过程|工具调用)\b.*$", RegexOptions.IgnoreCase)]
    private static partial Regex TechnicalLine();

    [GeneratedRegex(@"https?://\S+", RegexOptions.IgnoreCase)]
    private static partial Regex Url();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}