using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FgoPet.Extensibility;

namespace FgoPet.Plugin.SocialSources.Platforms;

/// <summary>
/// B站 reader: keyword video search, video detail, popular list and top-level comments.
///
/// <para>
/// This reader is <b>disabled by default</b> and must be enabled by an explicit local grant. It talks to
/// public endpoints that the platform documents for its own web client, not to the official Open Platform
/// (which exposes no content search API and does not admit individual developers). The operator of this
/// deployment has accepted that decision and its consequences; see the module README before enabling it.
/// </para>
///
/// <para>
/// Anonymous read access requires a device cookie pair (buvid3/buvid4) obtained once from the public
/// fingerprint endpoint; comments additionally require the locally computed <c>wbi</c> request signature.
/// Neither involves an account, password or user login.
/// </para>
/// </summary>
internal sealed class BilibiliReader : ISocialSourceReader
{
    private const string ApiRoot = "https://api.bilibili.com";
    private const string FingerUrl = ApiRoot + "/x/frontend/finger/spi";
    private const string NavUrl = ApiRoot + "/x/web-interface/nav";

    // Fixed table used to derive the wbi mixing key from the two images the nav endpoint publishes.
    private static readonly int[] MixinTable =
    [
        46, 47, 18, 2, 53, 8, 23, 32, 15, 50, 10, 31, 58, 3, 45, 35, 27, 43, 5, 49, 33, 9, 42, 19,
        29, 28, 14, 39, 12, 38, 41, 13, 37, 48, 7, 16, 24, 55, 40, 61, 26, 17, 0, 1, 60, 51, 30, 4,
        22, 25, 54, 21, 56, 59, 6, 63, 57, 62, 11, 36, 20, 34, 44, 52,
    ];

    private readonly SocialHttpGateway _gateway;
    private readonly SemaphoreSlim _bootstrapGate = new(1, 1);
    private string? _deviceCookie;
    private string? _mixinKey;

    public BilibiliReader(SocialHttpGateway gateway) => _gateway = gateway;

    public string Platform => SocialSourceSettings.Bilibili;

    public ToolAvailability Readiness => new(true);

    public async Task<SocialSourceResult> SearchAsync(string query, int maxResults, CancellationToken token)
    {
        var url = ApiRoot + "/x/web-interface/search/type?search_type=video&page=1&keyword="
            + Uri.EscapeDataString(query);
        var payload = await SendApiAsync(url, token).ConfigureAwait(false);
        var rows = new List<SocialSource>();
        if (payload.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in result.EnumerateArray())
            {
                if (rows.Count == maxResults) break;
                if (MapVideo(row) is { } video) rows.Add(video);
            }
        }
        return Materialize(rows, "bilibili_search", payload.GetRawText());
    }

    public async Task<SocialSourceResult> ReadAsync(string reference, CancellationToken token)
    {
        var bvid = BvidFrom(reference);
        var payload = await SendApiAsync(ApiRoot + "/x/web-interface/view?bvid=" + Uri.EscapeDataString(bvid),
            token).ConfigureAwait(false);
        var rows = new List<SocialSource>();
        if (MapDetail(payload) is { } video) rows.Add(video);
        return Materialize(rows, "bilibili_video", payload.GetRawText());
    }

    /// <summary>Accepts a /video/BV… URL or a bare BV id.</summary>
    internal static string BvidFrom(string reference)
    {
        var text = reference.Trim();
        if (IsBvid(text)) return text;
        if (!SocialSourceText.SafeUrl(text)) throw new SocialSourceException("SOCIAL_REFERENCE_INVALID");
        var uri = new Uri(text, UriKind.Absolute);
        if (!uri.Host.EndsWith("bilibili.com", StringComparison.OrdinalIgnoreCase))
            throw new SocialSourceException("SOCIAL_REFERENCE_INVALID");
        var match = System.Text.RegularExpressions.Regex.Match(uri.AbsolutePath, "(BV[0-9A-Za-z]{10})",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        if (!match.Success) throw new SocialSourceException("SOCIAL_REFERENCE_INVALID");
        return match.Groups[1].Value;
    }

    internal static bool IsBvid(string? value) => value is { Length: 12 }
        && value[0] == 'B' && value[1] == 'V' && value.Skip(2).All(char.IsAsciiLetterOrDigit);

    /// <summary>
    /// One API call: ensures the device cookie and mixing key, then classifies the envelope. Codes the
    /// platform uses to refuse anonymous traffic map to distinct safe failures rather than empty results.
    /// </summary>
    private async Task<JsonElement> SendApiAsync(string url, CancellationToken token)
    {
        var (cookie, mixin) = await BootstrapAsync(token).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
        SocialHttpGateway.AddCommonHeaders(request, cookie);
        var (status, body) = await _gateway.SendAsync(request, token).ConfigureAwait(false);
        if ((int)status == 412) throw new SocialSourceException("SOCIAL_RATE_LIMITED");
        if (status is System.Net.HttpStatusCode.TooManyRequests)
            throw new SocialSourceException("SOCIAL_RATE_LIMITED");
        if (status is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            throw new SocialSourceException("SOCIAL_ACCESS_DENIED");
        if (status != System.Net.HttpStatusCode.OK) throw new SocialSourceException("SOCIAL_NETWORK_FAILED");

        JsonDocument document;
        try { document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 64 }); }
        catch (JsonException) { throw new SocialSourceException("SOCIAL_RESPONSE_INVALID"); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("code", out var code) || !code.TryGetInt32(out var value))
                throw new SocialSourceException("SOCIAL_RESPONSE_INVALID");
            if (value != 0)
            {
                throw new SocialSourceException(value switch
                {
                    -412 or -799 => "SOCIAL_RATE_LIMITED",
                    -401 or -403 => "SOCIAL_ACCESS_DENIED",
                    -404 => "SOCIAL_NOT_FOUND",
                    _ => "SOCIAL_UPSTREAM_REFUSED",
                });
            }
            if (!root.TryGetProperty("data", out var data)) throw new SocialSourceException("SOCIAL_RESPONSE_INVALID");
            _ = mixin;
            return data.Clone();
        }
    }

    /// <summary>Fetches the anonymous device cookie pair and the wbi mixing key once per plugin lifetime.</summary>
    private async Task<(string Cookie, string Mixin)> BootstrapAsync(CancellationToken token)
    {
        if (_deviceCookie is { } existing && _mixinKey is { } existingMixin) return (existing, existingMixin);
        await _bootstrapGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_deviceCookie is { } cached && _mixinKey is { } cachedMixin) return (cached, cachedMixin);

            using var finger = new HttpRequestMessage(HttpMethod.Get, FingerUrl);
            request_headers(finger);
            var (fingerStatus, fingerBody) = await _gateway.SendAsync(finger, token).ConfigureAwait(false);
            if (fingerStatus != System.Net.HttpStatusCode.OK)
                throw new SocialSourceException("SOCIAL_NETWORK_FAILED");
            var device = ParseDeviceCookie(fingerBody)
                ?? throw new SocialSourceException("SOCIAL_RESPONSE_INVALID");

            using var nav = new HttpRequestMessage(HttpMethod.Get, NavUrl);
            request_headers(nav);
            SocialHttpGateway.AddCommonHeaders(nav, device);
            var (navStatus, navBody) = await _gateway.SendAsync(nav, token).ConfigureAwait(false);
            if (navStatus != System.Net.HttpStatusCode.OK)
                throw new SocialSourceException("SOCIAL_NETWORK_FAILED");
            var mixin = ParseMixinKey(navBody) ?? throw new SocialSourceException("SOCIAL_RESPONSE_INVALID");

            _deviceCookie = device;
            _mixinKey = mixin;
            return (device, mixin);

            static void request_headers(HttpRequestMessage message)
            {
                message.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
                SocialHttpGateway.AddCommonHeaders(message, cookie: null);
            }
        }
        finally { _bootstrapGate.Release(); }
    }

    /// <summary>Reads the anonymous device cookie pair the fingerprint endpoint returns.</summary>
    internal static string? ParseDeviceCookie(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 16 });
            if (!document.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Object) return null;
            var third = XiaoheiheReader.Text(data, "b_3");
            var fourth = XiaoheiheReader.Text(data, "b_4");
            if (string.IsNullOrWhiteSpace(third)) return null;
            return string.IsNullOrWhiteSpace(fourth)
                ? "buvid3=" + third
                : "buvid3=" + third + "; buvid4=" + fourth;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Derives the wbi mixing key from the two image names the nav endpoint publishes.</summary>
    internal static string? ParseMixinKey(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 32 });
            if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                return null;
            if (!data.TryGetProperty("wbi_img", out var images) || images.ValueKind != JsonValueKind.Object)
                return null;
            var source = ImageStem(XiaoheiheReader.Text(images, "img_url"))
                + ImageStem(XiaoheiheReader.Text(images, "sub_url"));
            if (source.Length < 64) return null;
            // The mixing key is the first 32 characters of the reordered image-name stem.
            return string.Concat(MixinTable.Take(32).Select(index => source[index]));
        }
        catch (JsonException) { return null; }

        static string ImageStem(string? url)
        {
            if (string.IsNullOrEmpty(url)) return string.Empty;
            var file = url[(url.LastIndexOf('/') + 1)..];
            var dot = file.LastIndexOf('.');
            return dot > 0 ? file[..dot] : file;
        }
    }

    /// <summary>Builds the signed query string for the wbi endpoints.</summary>
    internal static string SignWbi(IReadOnlyDictionary<string, string> parameters, string mixinKey, long timestamp)
    {
        var ordered = parameters
            .Where(pair => pair.Key is not ("w_rid" or "wts"))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Key + "=" + Uri.EscapeDataString(Sanitize(pair.Value)));
        var query = string.Join('&', ordered) + "&wts=" + timestamp.ToString(CultureInfo.InvariantCulture);
        var rid = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(query + mixinKey))).ToLowerInvariant();
        return query + "&w_rid=" + rid;

        // The wbi contract strips these characters before signing.
        static string Sanitize(string value) => new(value.Where(character => character is not ('!' or '\'' or '(' or ')' or '*')).ToArray());
    }

    private static SocialSource? MapVideo(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object) return null;
        var bvid = XiaoheiheReader.Text(row, "bvid");
        var rawTitle = XiaoheiheReader.Text(row, "title");
        if (bvid is null || !IsBvid(bvid) || string.IsNullOrWhiteSpace(rawTitle)) return null;

        var title = SocialSourceText.Clip(SocialSourceText.StripTags(rawTitle), SocialSourceText.MaxTitleRunes,
            out var titleTruncated);
        if (!SocialSourceText.SafeText(title, allowLineBreaks: false)) return null;

        var rawDescription = XiaoheiheReader.Text(row, "description") ?? string.Empty;
        var summary = SocialSourceText.StripTags(rawDescription);
        if (!SocialSourceText.SafeText(summary, allowLineBreaks: true)) summary = string.Empty;
        var snippet = SocialSourceText.Clip(summary, SocialSourceText.MaxSummaryRunes, out var summaryTruncated);

        var url = "https://www.bilibili.com/video/" + bvid;
        if (!SocialSourceText.SafeUrl(url)) return null;

        return new SocialSource(SocialSourceSettings.Bilibili, title, url, snippet,
            Author: XiaoheiheReader.Text(row, "author"),
            PublishedAt: UnixTime(row, "pubdate"),
            Popularity: XiaoheiheReader.NumberOrString(row, "play") is { } play ? play + " plays" : null,
            TitleTruncated: titleTruncated, SummaryTruncated: summaryTruncated);
    }

    private static SocialSource? MapDetail(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) return null;
        var bvid = XiaoheiheReader.Text(data, "bvid");
        var rawTitle = XiaoheiheReader.Text(data, "title");
        if (bvid is null || !IsBvid(bvid) || string.IsNullOrWhiteSpace(rawTitle)) return null;

        var title = SocialSourceText.Clip(SocialSourceText.StripTags(rawTitle), SocialSourceText.MaxTitleRunes,
            out var titleTruncated);
        if (!SocialSourceText.SafeText(title, allowLineBreaks: false)) return null;
        var summary = SocialSourceText.StripTags(XiaoheiheReader.Text(data, "desc") ?? string.Empty);
        if (!SocialSourceText.SafeText(summary, allowLineBreaks: true)) summary = string.Empty;
        var snippet = SocialSourceText.Clip(summary, SocialSourceText.MaxSummaryRunes, out var summaryTruncated);
        var url = "https://www.bilibili.com/video/" + bvid;
        if (!SocialSourceText.SafeUrl(url)) return null;

        string? author = null;
        if (data.TryGetProperty("owner", out var owner) && owner.ValueKind == JsonValueKind.Object)
            author = XiaoheiheReader.Text(owner, "name");

        string? popularity = null;
        if (data.TryGetProperty("stat", out var stat) && stat.ValueKind == JsonValueKind.Object)
            popularity = string.Join(", ", new[]
            {
                XiaoheiheReader.NumberOrString(stat, "view") is { } view ? view + " plays" : null,
                XiaoheiheReader.NumberOrString(stat, "like") is { } like ? like + " likes" : null,
                XiaoheiheReader.NumberOrString(stat, "reply") is { } reply ? reply + " replies" : null,
            }.Where(part => part is not null));
        if (popularity is { Length: 0 }) popularity = null;

        return new SocialSource(SocialSourceSettings.Bilibili, title, url, snippet, author,
            UnixTime(data, "pubdate"), popularity, titleTruncated, summaryTruncated);
    }

    private static string? UnixTime(JsonElement element, string name)
    {
        var raw = XiaoheiheReader.NumberOrString(element, name);
        return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            && seconds is > 1_000_000_000 and < 4_000_000_000
            ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime
                .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
            : null;
    }

    private static SocialSourceResult Materialize(List<SocialSource> rows, string coverage, string body) =>
        new(rows, DateTimeOffset.UtcNow.ToString("O"), SocialSourceText.Sha256Hex(body),
            "response_snapshot_sha256", coverage, Exhaustive: false, Truncated: false);
}
