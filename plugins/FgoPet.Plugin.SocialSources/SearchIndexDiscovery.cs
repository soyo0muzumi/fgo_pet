using System.Text.Json;
using FgoPet.Extensibility;

namespace FgoPet.Plugin.SocialSources;

/// <summary>
/// Discovers platform links through a public search index when the platform itself refuses anonymous
/// keyword search. This is a discovery step only: it returns titles, snippets and URLs, and the caller
/// opens any result it needs with the platform reader. The query is sent to the search provider, which
/// is a different service from the platform, so every tool description discloses it.
/// </summary>
internal sealed class SearchIndexDiscovery(SocialHttpGateway gateway)
{
    private const string Endpoint = "https://html.duckduckgo.com/html/";
    private const int MaxBodyBytes = 512 * 1024;

    /// <summary>Returns index results whose URL matches <paramref name="urlPattern"/>.</summary>
    internal async Task<List<SocialSource>> DiscoverAsync(string platform, string query,
        System.Text.RegularExpressions.Regex urlPattern, int maxResults, CancellationToken token)
    {
        // The HTML endpoint answers GET with results and rejects POST with 403.
        var target = Endpoint + "?q=" + Uri.EscapeDataString(query);
        using var request = new HttpRequestMessage(HttpMethod.Get, target);
        SocialHttpGateway.AddCommonHeaders(request, cookie: null);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/html"));

        var (status, html) = await gateway.SendAsync(request, token).ConfigureAwait(false);
        if (status is System.Net.HttpStatusCode.TooManyRequests)
            throw new SocialSourceException("SOCIAL_RATE_LIMITED");
        if (status is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized)
            throw new SocialSourceException("SOCIAL_ACCESS_DENIED");
        if (status != System.Net.HttpStatusCode.OK) throw new SocialSourceException("SOCIAL_NETWORK_FAILED");
        if (html.Length > MaxBodyBytes) throw new SocialSourceException("SOCIAL_RESPONSE_TOO_LARGE");
        if (html.Contains("anomaly-modal", StringComparison.Ordinal)
            || html.Contains("challenge-form", StringComparison.Ordinal))
            throw new SocialSourceException("SOCIAL_RATE_LIMITED");

        var rows = new List<SocialSource>();
        foreach (var match in ResultPattern().Matches(html).Cast<System.Text.RegularExpressions.Match>())
        {
            if (rows.Count == maxResults) break;
            var url = ResolveHref(match.Groups["href"].Value);
            if (url is null || !urlPattern.IsMatch(url)) continue;
            if (!SocialSourceText.SafeUrl(url)) continue;

            var title = SocialSourceText.StripTags(match.Groups["title"].Value).Trim();
            if (title.Length == 0 || !SocialSourceText.SafeText(title, allowLineBreaks: false)) continue;
            var snippet = SocialSourceText.StripTags(match.Groups["snippet"].Value).Trim();
            if (!SocialSourceText.SafeText(snippet, allowLineBreaks: true)) snippet = string.Empty;

            var clippedTitle = SocialSourceText.Clip(title, SocialSourceText.MaxTitleRunes, out var titleTruncated);
            var clippedSnippet = SocialSourceText.Clip(snippet, SocialSourceText.MaxSummaryRunes,
                out var summaryTruncated);
            rows.Add(new SocialSource(platform, clippedTitle, url, clippedSnippet, null, null, null,
                titleTruncated, summaryTruncated));
        }
        return rows;
    }

    /// <summary>The index wraps result links in a redirect; extract the real destination.</summary>
    private static string? ResolveHref(string href)
    {
        var decoded = System.Net.WebUtility.HtmlDecode(href.Trim());
        if (decoded.Length == 0) return null;
        var marker = decoded.IndexOf("uddg=", StringComparison.Ordinal);
        if (marker >= 0)
        {
            var value = decoded[(marker + 5)..];
            var ampersand = value.IndexOf('&');
            if (ampersand >= 0) value = value[..ampersand];
            decoded = Uri.UnescapeDataString(value);
        }
        return decoded.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? decoded : null;
    }

    private static System.Text.RegularExpressions.Regex ResultPattern() => CachedPattern;

    private static readonly System.Text.RegularExpressions.Regex CachedPattern = new(
        """class="result__a"[^>]*href="(?<href>[^"]+)"[^>]*>(?<title>.*?)</a>.*?class="result__snippet"[^>]*>(?<snippet>.*?)</(?:a|div|span)>""",
        System.Text.RegularExpressions.RegexOptions.Singleline
        | System.Text.RegularExpressions.RegexOptions.IgnoreCase
        | System.Text.RegularExpressions.RegexOptions.Compiled);
}
