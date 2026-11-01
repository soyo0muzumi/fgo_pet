using System.Security.Cryptography;
using System.Text;
using FgoPet.Extensibility;

namespace FgoPet.Plugin.SocialSources;

/// <summary>One normalized read-only source row. Never a permission, fact or confirmed memory.</summary>
public sealed record SocialSource(string Platform, string Title, string Url, string Summary,
    string? Author, string? PublishedAt, string? Popularity, bool TitleTruncated = false, bool SummaryTruncated = false);

/// <summary>A bounded page of normalized rows plus provenance the model must keep attached.</summary>
public sealed record SocialSourceResult(IReadOnlyList<SocialSource> Results, string CapturedAtUtc,
    string SourceVersion, string SourceVersionKind, string Coverage, bool Exhaustive, bool Truncated);

/// <summary>
/// Platform-specific reader. Implementations own their wire protocol only: no grant, scope, tool or
/// scheduling decisions. Every call is one bounded attempt with no retry.
/// </summary>
public interface ISocialSourceReader
{
    string Platform { get; }

    /// <summary>Readiness that performs no network I/O.</summary>
    ToolAvailability Readiness { get; }

    Task<SocialSourceResult> SearchAsync(string query, int maxResults, CancellationToken token);

    /// <summary>Reads one item by the identifier form returned in <see cref="SocialSource.Url"/>.</summary>
    Task<SocialSourceResult> ReadAsync(string reference, CancellationToken token);
}

/// <summary>Allowlisted, body-free failures. Never carries upstream text, stderr or a local path.</summary>
public sealed class SocialSourceException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

internal static class SocialSourceText
{
    internal const int MaxTitleRunes = 256;
    internal const int MaxSummaryRunes = 2000;
    internal const int MaxUrlLength = 2048;

    /// <summary>True for absolute http(s) URLs with a host and no embedded credentials.</summary>
    internal static bool SafeUrl(string? value) => value is { Length: > 0 and <= MaxUrlLength }
        && !value.Any(char.IsControl)
        && Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https" && uri.Host.Length > 0 && uri.UserInfo.Length == 0;

    internal static bool SafeText(string? value, bool allowLineBreaks)
    {
        if (value is null) return false;
        foreach (var character in value)
        {
            if (char.IsControl(character) && !(allowLineBreaks && character is '\r' or '\n' or '\t')) return false;
            if (char.IsSurrogate(character) && !char.IsHighSurrogate(character) && !char.IsLowSurrogate(character))
                return false;
        }
        return !value.Contains('\uFFFD');
    }

    internal static string Clip(string value, int limit, out bool truncated)
    {
        truncated = value.EnumerateRunes().Count() > limit;
        return truncated ? string.Concat(value.EnumerateRunes().Take(limit).Select(rune => rune.ToString())) : value;
    }

    /// <summary>Removes the &lt;em class="keyword"&gt; highlighting that platforms inject into titles.</summary>
    internal static string StripTags(string value)
    {
        var text = new StringBuilder(value.Length);
        var inside = false;
        foreach (var character in value)
        {
            if (character == '<') { inside = true; continue; }
            if (character == '>') { inside = false; continue; }
            if (!inside) text.Append(character);
        }
        return System.Net.WebUtility.HtmlDecode(text.ToString());
    }

    internal static string Sha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    internal static string Correlation(string? value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value ?? Guid.NewGuid().ToString("N"))))[..24];
}
