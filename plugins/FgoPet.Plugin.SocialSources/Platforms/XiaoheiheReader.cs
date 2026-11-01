using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FgoPet.Extensibility;

namespace FgoPet.Plugin.SocialSources.Platforms;

/// <summary>
/// 小黑盒 reader. Reads public news feeds and single posts, and discovers post links for keyword search.
/// The web client's request signature (<c>hkey</c>) and fixed client parameters are reproduced from
/// observed protocol facts (see THIRD-PARTY-NOTICES.md); they identify a client build, not a user or
/// session. No service credential and no account are involved, and the platform answers an anonymous
/// single-post read with <c>show_captcha</c>.
/// </summary>
internal sealed class XiaoheiheReader : ISocialSourceReader
{
    private const string ApiRoot = "https://api.xiaoheihe.cn";
    internal const string WebVersionRead = "2.5";
    internal const string WebVersionSearch = "3.0";
    // Character table used by the client-side obfuscation inside the hkey construction.
    private const string Alphabet = "AB45STUVWZEFGJ6CH01D237IXYPQRKLMN89";

    private readonly SocialHttpGateway _gateway;
    private readonly SearchIndexDiscovery _discovery;

    public XiaoheiheReader(SocialHttpGateway gateway)
    {
        _gateway = gateway;
        _discovery = new SearchIndexDiscovery(gateway);
    }

    public string Platform => SocialSourceSettings.Xiaoheihe;

    /// <summary>Signature generation is local and needs no configuration, so the reader is always ready.</summary>
    public ToolAvailability Readiness => new(true);

    /// <summary>
    /// The platform's own keyword endpoint answers with a captcha challenge for anonymous traffic
    /// (`show_captcha`) and its legacy search routes report `expired`, so discovery runs through a public
    /// search index restricted to post URLs. Callers then read any result with <see cref="ReadAsync"/>.
    /// </summary>
    public async Task<SocialSourceResult> SearchAsync(string query, int maxResults, CancellationToken token)
    {
        var rows = await _discovery.DiscoverAsync(SocialSourceSettings.Xiaoheihe,
            "site:xiaoheihe.cn " + query, PostLinkPattern(), maxResults, token).ConfigureAwait(false);
        return new(rows, DateTimeOffset.UtcNow.ToString("O"),
            SocialSourceText.Sha256Hex(string.Join('\n', rows.Select(row => row.Url))),
            "discovery_url_set_sha256", "search_index_discovery", Exhaustive: false,
            Truncated: rows.Count >= maxResults);
    }

    private static System.Text.RegularExpressions.Regex PostLinkPattern() => CachedPostLink;

    private static readonly System.Text.RegularExpressions.Regex CachedPostLink = new(
        @"^https://(?:www\.)?xiaoheihe\.cn/(?:app/)?bbs/link/\d+$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    public async Task<SocialSourceResult> ReadAsync(string reference, CancellationToken token)
    {
        var linkId = LinkIdFrom(reference);
        var path = "/bbs/app/link/tree";
        var body = await GetAsync(path, "link_id=" + Uri.EscapeDataString(linkId), token).ConfigureAwait(false);
        return Materialize(ParseRows(body, "link", 1), "xiaoheihe_post", body);
    }

    private async Task<string> GetAsync(string path, string query, CancellationToken token)
    {
        var url = ApiRoot + path + "?" + WebQuery(null, WebVersionRead) + "&" + query + "&" + Sign(path);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Referer", "https://www.xiaoheihe.cn/");
        request.Headers.TryAddWithoutValidation("Origin", "https://www.xiaoheihe.cn");
        SocialHttpGateway.AddCommonHeaders(request, cookie: null);
        var (status, body) = await _gateway.SendAsync(request, token).ConfigureAwait(false);
        EnsureStatus(status);
        return body;
    }

    /// <summary>
    /// Fixed web-client parameters. They are part of the protocol rather than preferences: the older
    /// mobile-client set produces an accepted-looking response with an empty result list.
    /// </summary>
    internal static string WebQuery(string? heyboxId, string webVersion) =>
        "os_type=web&app=heybox&client_type=web&version=999.0.4&web_version=" + webVersion
        + "&x_client_type=web&x_app=heybox_website&heybox_id=" + (heyboxId ?? string.Empty)
        + "&x_os_type=Windows&device_info=Chrome";

    private static void EnsureStatus(System.Net.HttpStatusCode status)
    {
        if ((int)status == 412 || status is System.Net.HttpStatusCode.TooManyRequests)
            throw new SocialSourceException("SOCIAL_RATE_LIMITED");
        if (status is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            throw new SocialSourceException("SOCIAL_ACCESS_DENIED");
        if (status != System.Net.HttpStatusCode.OK) throw new SocialSourceException("SOCIAL_NETWORK_FAILED");
    }

    /// <summary>Accepts a share/post URL or a bare numeric post id.</summary>
    internal static string LinkIdFrom(string reference)
    {
        var text = reference.Trim();
        if (text.Length is > 0 and <= 64 && text.All(char.IsAsciiDigit)) return text;
        if (!SocialSourceText.SafeUrl(text)) throw new SocialSourceException("SOCIAL_REFERENCE_INVALID");
        var uri = new Uri(text, UriKind.Absolute);
        if (uri.Host is not ("www.xiaoheihe.cn" or "xiaoheihe.cn" or "api.xiaoheihe.cn"))
            throw new SocialSourceException("SOCIAL_REFERENCE_INVALID");
        var fromQuery = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Where(pair => pair.Length == 2 && pair[0] is "link_id" or "linkid")
            .Select(pair => pair[1])
            .FirstOrDefault(value => value.Length > 0 && value.All(char.IsAsciiDigit));
        if (fromQuery is not null) return fromQuery;
        var segment = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (segment is not { Length: > 0 } || !segment.All(char.IsAsciiDigit))
            throw new SocialSourceException("SOCIAL_REFERENCE_INVALID");
        return segment;
    }

    private static List<SocialSource> ParseRows(string body, string field, int maxResults)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 64 }); }
        catch (JsonException) { throw new SocialSourceException("SOCIAL_RESPONSE_INVALID"); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new SocialSourceException("SOCIAL_RESPONSE_INVALID");
            var state = Text(root, "status");
            if (state is not "ok")
            {
                // "show_captcha" / "failed" / "expired" are upstream refusals, never an empty result.
                throw new SocialSourceException(state is "show_captcha" ? "SOCIAL_CAPTCHA_REQUIRED"
                    : "SOCIAL_UPSTREAM_REFUSED");
            }
            if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
                throw new SocialSourceException("SOCIAL_RESPONSE_INVALID");
            if (!result.TryGetProperty(field, out var rows)) throw new SocialSourceException("SOCIAL_RESPONSE_INVALID");

            var collected = new List<SocialSource>();
            if (rows.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in rows.EnumerateArray())
                {
                    if (collected.Count == maxResults) break;
                    if (MapRow(row) is { } mapped) collected.Add(mapped);
                }
            }
            else if (rows.ValueKind == JsonValueKind.Object && MapRow(rows) is { } single)
            {
                collected.Add(single);
            }
            return collected;
        }
    }

    private static SocialSource? MapRow(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object) return null;
        var linkId = NumberOrString(row, "linkid") ?? NumberOrString(row, "link_id");
        var rawTitle = Text(row, "title");
        if (linkId is null || string.IsNullOrWhiteSpace(rawTitle)) return null;

        var title = SocialSourceText.Clip(SocialSourceText.StripTags(rawTitle), SocialSourceText.MaxTitleRunes,
            out var titleTruncated);
        if (!SocialSourceText.SafeText(title, allowLineBreaks: false)) return null;

        var rawSummary = Text(row, "description") ?? Text(row, "content_short") ?? string.Empty;
        var summary = SocialSourceText.StripTags(rawSummary);
        if (!SocialSourceText.SafeText(summary, allowLineBreaks: true)) summary = string.Empty;
        var snippet = SocialSourceText.Clip(summary, SocialSourceText.MaxSummaryRunes, out var summaryTruncated);

        var url = "https://www.xiaoheihe.cn/app/bbs/link/" + linkId;
        if (!SocialSourceText.SafeUrl(url)) return null;

        var user = row.TryGetProperty("user", out var userValue) && userValue.ValueKind == JsonValueKind.Object
            ? userValue : (JsonElement?)null;
        return new SocialSource(SocialSourceSettings.Xiaoheihe, title, url, snippet,
            Author: user is { } account ? Text(account, "username") ?? Text(account, "nickname") : null,
            PublishedAt: UnixTime(row, "create_at") ?? UnixTime(row, "modify_at"),
            Popularity: NumberOrString(row, "comment_num") is { } comments ? comments + " comments" : null,
            TitleTruncated: titleTruncated, SummaryTruncated: summaryTruncated);
    }

    internal static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    internal static string? NumberOrString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetInt64(out var number)
                ? number.ToString(CultureInfo.InvariantCulture) : null,
            JsonValueKind.String => value.GetString(),
            _ => null,
        };
    }

    internal static string? UnixTime(JsonElement element, string name)
    {
        var raw = NumberOrString(element, name);
        return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            && seconds is > 1_000_000_000 and < 4_000_000_000
            ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime
                .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
            : null;
    }

    /// <summary>Reads one cookie value without decoding, reordering or echoing the rest of the header.</summary>
    internal static string? CookieValue(string cookie, string name)
    {
        foreach (var pair in cookie.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0) continue;
            if (!pair.AsSpan(0, separator).Trim().Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            return pair[(separator + 1)..];
        }
        return null;
    }

    /// <summary>
    /// The account id the web client sends as <c>heybox_id</c>, derived only from the stored session
    /// cookie. A missing or unexpected value falls back to the anonymous empty form.
    /// </summary>
    internal static string HeyboxIdFromCookie(string cookie) =>
        CookieValue(cookie, "heybox_id") is { Length: > 0 and <= 20 } id && id.All(char.IsAsciiDigit)
            ? id : string.Empty;

    private static SocialSourceResult Materialize(List<SocialSource> rows, string coverage, string body) =>
        new(rows, DateTimeOffset.UtcNow.ToString("O"), SocialSourceText.Sha256Hex(body),
            "response_snapshot_sha256", coverage, Exhaustive: false, Truncated: false);

    /// <summary>
    /// Local request signature, ported from the MIT-licensed xiaoheihe-mcp hkey implementation (see
    /// THIRD-PARTY-NOTICES.md). The server rejects the older digits-only construction with
    /// <c>非法请求</c>, so this exact mixing is part of the protocol rather than a detail.
    /// </summary>
    internal static string Sign(string path, long? timestamp = null, string? nonce = null)
    {
        var seconds = timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        nonce ??= Md5Hex(seconds.ToString(CultureInfo.InvariantCulture)
            + Random.Shared.NextDouble().ToString("G17", CultureInfo.InvariantCulture)).ToUpperInvariant();
        var merged = Interleave(
            ProjectChars((seconds + 1).ToString(CultureInfo.InvariantCulture), Alphabet[..^2]),
            ProjectChars(NormalizePath(path), Alphabet),
            ProjectChars(nonce, Alphabet));
        var hash = Md5Hex(merged.Length > 20 ? merged[..20] : merged);
        var tail = hash[^6..].Select(character => (int)character).ToArray();
        var mixed = MixColumns(tail[..4]);
        var checksum = ((mixed.Sum() + tail[4] + tail[5]) % 100)
            .ToString(CultureInfo.InvariantCulture).PadLeft(2, '0');
        return "hkey=" + ProjectChars(hash[..5], Alphabet[..^4]) + checksum
            + "&_time=" + seconds.ToString(CultureInfo.InvariantCulture) + "&nonce=" + nonce;
    }

    /// <summary>Round-robin merge of the three projected components: a0 b0 c0 a1 b1 c1 …</summary>
    private static string Interleave(string first, string second, string third)
    {
        var merged = new StringBuilder(first.Length + second.Length + third.Length);
        var length = Math.Max(first.Length, Math.Max(second.Length, third.Length));
        for (var index = 0; index < length; index++)
        {
            if (index < first.Length) merged.Append(first[index]);
            if (index < second.Length) merged.Append(second[index]);
            if (index < third.Length) merged.Append(third[index]);
        }
        return merged.ToString();
    }

    private static string ProjectChars(string value, string table)
    {
        var projected = new StringBuilder(value.Length);
        foreach (var character in value) projected.Append(table[character % table.Length]);
        return projected.ToString();
    }

    private static string NormalizePath(string path) =>
        "/" + string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries)) + "/";

    /// <summary>One column-mixing round over the first four bytes of the hash tail.</summary>
    private static int[] MixColumns(int[] value) =>
    [
        G(value[0]) ^ Y(value[1]) ^ R(value[2]) ^ Q(value[3]),
        Q(value[0]) ^ G(value[1]) ^ Y(value[2]) ^ R(value[3]),
        R(value[0]) ^ Q(value[1]) ^ G(value[2]) ^ Y(value[3]),
        Y(value[0]) ^ R(value[1]) ^ Q(value[2]) ^ G(value[3]),
    ];

    /// <summary>GF(2^8) doubling with the AES polynomial.</summary>
    private static int Xtime(int value) =>
        (value & 0x80) != 0 ? ((value << 1) ^ 0x1B) & 0xFF : (value << 1) & 0xFF;

    private static int Q(int value) => Xtime(value) ^ value;

    private static int R(int value) => Q(Xtime(value));

    private static int Y(int value) => R(Q(Xtime(value)));

    private static int G(int value) => Y(value) ^ R(value) ^ Q(value);

    private static string Md5Hex(string value) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
