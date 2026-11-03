using System.Globalization;
using System.Text.Json;
using FgoPet.Extensibility;

namespace FgoPet.Plugin.SocialSources.Platforms;

/// <summary>
/// 小红书 reader. Keyword search requires an authenticated web session and is therefore not offered;
/// this reader covers the publicly served note page only, which carries its data in the server-rendered
/// page state. No cookie, signature or account is used.
/// </summary>
internal sealed class XiaohongshuReader : ISocialSourceReader
{
    private const string ExploreRoot = "https://www.xiaohongshu.com/explore/";
    private const string SitemapUrl = "https://www.xiaohongshu.com/sitemap.xml";
    private static readonly System.Text.RegularExpressions.Regex StatePattern = new(
        @"window\.__INITIAL_STATE__\s*=\s*", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly System.Text.RegularExpressions.Regex NoteIdPattern = new(
        @"^[0-9a-f]{24}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private readonly SocialHttpGateway _gateway;
    private readonly SearchIndexDiscovery _discovery;

    public XiaohongshuReader(SocialHttpGateway gateway)
    {
        _gateway = gateway;
        _discovery = new SearchIndexDiscovery(gateway);
    }

    public string Platform => SocialSourceSettings.Xiaohongshu;

    public ToolAvailability Readiness => new(true);

    /// <summary>
    /// The platform refuses anonymous keyword search, so discovery runs through a public search index
    /// restricted to note URLs. Callers then read any result with <see cref="ReadAsync"/>.
    /// </summary>
    public async Task<SocialSourceResult> SearchAsync(string query, int maxResults, CancellationToken token)
    {
        var rows = await _discovery.DiscoverAsync(SocialSourceSettings.Xiaohongshu,
            "site:xiaohongshu.com/explore " + query, NoteLinkPattern(), maxResults, token)
            .ConfigureAwait(false);
        return new(rows, DateTimeOffset.UtcNow.ToString("O"),
            SocialSourceText.Sha256Hex(string.Join('\n', rows.Select(row => row.Url))),
            "discovery_url_set_sha256", "search_index_discovery", Exhaustive: false,
            Truncated: rows.Count >= maxResults);
    }

    private static System.Text.RegularExpressions.Regex NoteLinkPattern() => CachedNoteLink;

    private static readonly System.Text.RegularExpressions.Regex CachedNoteLink = new(
        @"^https://www\.xiaohongshu\.com/explore/[0-9a-f]{24}$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    public async Task<SocialSourceResult> ReadAsync(string reference, CancellationToken token)
    {
        var noteId = NoteIdFrom(reference);
        using var request = new HttpRequestMessage(HttpMethod.Get, ExploreRoot + noteId);
        request.Headers.TryAddWithoutValidation("Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        request.Headers.TryAddWithoutValidation("Referer", "https://www.xiaohongshu.com/");
        SocialHttpGateway.AddCommonHeaders(request, cookie: null);
        var (status, body) = await _gateway.SendAsync(request, token).ConfigureAwait(false);
        if (status is System.Net.HttpStatusCode.NotFound) throw new SocialSourceException("SOCIAL_NOT_FOUND");
        if ((int)status == 412 || status is System.Net.HttpStatusCode.TooManyRequests)
            throw new SocialSourceException("SOCIAL_RATE_LIMITED");
        if (status is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized)
            throw new SocialSourceException("SOCIAL_ACCESS_DENIED");
        if (status != System.Net.HttpStatusCode.OK) throw new SocialSourceException("SOCIAL_NETWORK_FAILED");

        var note = ParseNoteCard(body);
        var rows = note is null ? [] : new List<SocialSource> { note };
        return new(rows, DateTimeOffset.UtcNow.ToString("O"), SocialSourceText.Sha256Hex(body),
            "page_snapshot_sha256", "public_note_page", Exhaustive: true, Truncated: false);
    }

    /// <summary>Accepts a full /explore/ URL or a bare 24-character lowercase hex note id.</summary>
    internal static string NoteIdFrom(string reference)
    {
        var text = reference.Trim();
        if (text.Length == 24 && NoteIdPattern.IsMatch(text)) return text;
        if (!SocialSourceText.SafeUrl(text)) throw new SocialSourceException("SOCIAL_REFERENCE_INVALID");
        var uri = new Uri(text, UriKind.Absolute);
        if (uri.Host is not ("www.xiaohongshu.com" or "xiaohongshu.com"))
            throw new SocialSourceException("SOCIAL_REFERENCE_INVALID");
        var segment = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(part => NoteIdPattern.IsMatch(part));
        if (segment is null) throw new SocialSourceException("SOCIAL_REFERENCE_INVALID");
        return segment;
    }

    /// <summary>Extracts the first note card from the server-rendered page state.</summary>
    internal static SocialSource? ParseNoteCard(string html)
    {
        var match = StatePattern.Match(html);
        if (!match.Success) return null;
        var rest = html.AsSpan(match.Index + match.Length);
        var end = rest.IndexOf("</script>");
        if (end > 0) rest = rest[..end];
        var raw = rest.ToString().TrimEnd().TrimEnd(';');
        // The page state is JavaScript, not strict JSON: it may contain `undefined` literals.
        raw = raw.Replace(":undefined", ":null", StringComparison.Ordinal)
            .Replace(": undefined", ": null", StringComparison.Ordinal);

        JsonDocument document;
        try { document = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 128 }); }
        catch (JsonException) { throw new SocialSourceException("SOCIAL_RESPONSE_INVALID"); }
        using (document)
        {
            if (!document.RootElement.TryGetProperty("note", out var noteSection)
                || noteSection.ValueKind != JsonValueKind.Object) return null;
            if (!noteSection.TryGetProperty("noteDetailMap", out var detail)
                || detail.ValueKind != JsonValueKind.Object) return null;
            foreach (var entry in detail.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object
                    || !entry.Value.TryGetProperty("note", out var note)
                    || note.ValueKind != JsonValueKind.Object) continue;
                var card = MapNote(note);
                if (card is not null) return card;
            }
            return null;
        }
    }

    private static SocialSource? MapNote(JsonElement note)
    {
        var noteId = XiaoheiheReader.Text(note, "noteId");
        var rawTitle = XiaoheiheReader.Text(note, "title") ?? string.Empty;
        var rawDescription = XiaoheiheReader.Text(note, "desc") ?? string.Empty;
        if (noteId is null || !NoteIdPattern.IsMatch(noteId)) return null;
        if (string.IsNullOrWhiteSpace(rawTitle) && string.IsNullOrWhiteSpace(rawDescription)) return null;

        var title = SocialSourceText.Clip(SocialSourceText.StripTags(
                string.IsNullOrWhiteSpace(rawTitle) ? FirstLine(rawDescription) : rawTitle),
            SocialSourceText.MaxTitleRunes, out var titleTruncated);
        if (!SocialSourceText.SafeText(title, allowLineBreaks: false)) return null;

        var summary = SocialSourceText.StripTags(rawDescription);
        if (!SocialSourceText.SafeText(summary, allowLineBreaks: true)) summary = string.Empty;
        var snippet = SocialSourceText.Clip(summary, SocialSourceText.MaxSummaryRunes, out var summaryTruncated);

        var url = ExploreRoot + noteId;
        if (!SocialSourceText.SafeUrl(url)) return null;

        string? author = null;
        if (note.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object)
            author = XiaoheiheReader.Text(user, "nickname") ?? XiaoheiheReader.Text(user, "nickName");

        string? popularity = null;
        if (note.TryGetProperty("interactInfo", out var interact) && interact.ValueKind == JsonValueKind.Object)
        {
            var liked = XiaoheiheReader.NumberOrString(interact, "likedCount");
            var collected = XiaoheiheReader.NumberOrString(interact, "collectedCount");
            var comments = XiaoheiheReader.NumberOrString(interact, "commentCount");
            popularity = string.Join(", ", new[] { liked is null ? null : liked + " likes",
                collected is null ? null : collected + " collects", comments is null ? null : comments + " comments" }
                .Where(part => part is not null));
            if (popularity.Length == 0) popularity = null;
        }

        string? published = null;
        var time = XiaoheiheReader.NumberOrString(note, "time");
        if (long.TryParse(time, NumberStyles.Integer, CultureInfo.InvariantCulture, out var milliseconds)
            && milliseconds is > 1_000_000_000_000 and < 4_000_000_000_000)
            published = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime
                .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

        return new SocialSource(SocialSourceSettings.Xiaohongshu, title, url, snippet, author, published,
            popularity, titleTruncated, summaryTruncated);
    }

    private static string FirstLine(string value)
    {
        var line = value.Split('\n').Select(part => part.Trim()).FirstOrDefault(part => part.Length > 0);
        return line ?? string.Empty;
    }
}
