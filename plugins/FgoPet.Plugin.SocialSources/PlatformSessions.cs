using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using FgoPet.Core.Secrets;
using FgoPet.Extensibility;
using FgoPet.Plugin.SocialSources.Platforms;

namespace FgoPet.Plugin.SocialSources;

/// <summary>
/// 小红书 authenticated search. The endpoint accepts a session cookie without a request signature —
/// verified by the error progression -101 (no session) → -100 (expired session) with no signature
/// headers at all — so this connection sends the stored cookie and nothing else is derived.
/// </summary>
internal sealed class XiaohongshuSessionConnection(ICredentialReader credentials, SocialHttpGateway gateway)
    : SocialSessionConnectionBase(SocialSourceSettings.Xiaohongshu, credentials, gateway)
{
    private const string SearchUri = "/api/sns/web/v1/search/notes";
    private const string SearchUrl = "https://edith.xiaohongshu.com" + SearchUri;

    protected override async Task<SocialSourceResult> SearchCoreAsync(string secret, string query, int maxResults,
        CancellationToken token)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            keyword = query,
            page = 1,
            page_size = Math.Clamp(maxResults, 1, 20),
            search_id = Guid.NewGuid().ToString("N")[..16],
            sort = "general",
            note_type = 0,
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, SearchUrl)
        {
            Content = new ByteArrayContent(payload),
        };
        request.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") { CharSet = "UTF-8" };
        request.Headers.TryAddWithoutValidation("Referer", "https://www.xiaohongshu.com/");
        request.Headers.TryAddWithoutValidation("Origin", "https://www.xiaohongshu.com");
        SocialHttpGateway.AddCommonHeaders(request, secret);
        var (_, body) = await SendAsync(request, token).ConfigureAwait(false);

        JsonDocument document;
        try { document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 64 }); }
        catch (JsonException) { throw new SocialSourceException("SOCIAL_RESPONSE_INVALID"); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("code", out var code)
                || !code.TryGetInt32(out var value))
                throw new SocialSourceException("SOCIAL_RESPONSE_INVALID");
            EnsureCode(value);
            var rows = new List<SocialSource>();
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    if (rows.Count == maxResults) break;
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var card = item.TryGetProperty("note_card", out var noteCard)
                        && noteCard.ValueKind == JsonValueKind.Object ? noteCard : item;
                    if (MapRow(SocialSourceSettings.Xiaohongshu, card) is not { } mapped) continue;
                    // A note id plus its xsec token is the addressable form the platform expects.
                    var noteId = XiaoheiheReader.Text(item, "id") ?? XiaoheiheReader.Text(card, "noteId");
                    if (noteId is null || noteId.Length != 24 || !noteId.All(char.IsAsciiHexDigitLower))
                        continue;
                    var tokenValue = XiaoheiheReader.Text(item, "xsec_token");
                    var url = "https://www.xiaohongshu.com/explore/" + noteId
                        + (string.IsNullOrEmpty(tokenValue) ? string.Empty : "?xsec_token=" + Uri.EscapeDataString(tokenValue));
                    if (!SocialSourceText.SafeUrl(url)) continue;
                    var author = mapped.Author;
                    var popularity = mapped.Popularity;
                    if (card.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object)
                        author = XiaoheiheReader.Text(user, "nickname") ?? author;
                    if (card.TryGetProperty("interact_info", out var interact)
                        && interact.ValueKind == JsonValueKind.Object)
                    {
                        var liked = XiaoheiheReader.NumberOrString(interact, "liked_count");
                        popularity = liked is null ? popularity : liked + " likes";
                    }
                    rows.Add(mapped with { Url = url, Author = author, Popularity = popularity });
                }
            }
            return Materialize(rows, "xiaohongshu_account_search", body);
        }
    }

    protected override async Task ProbeAsync(string secret, CancellationToken token)
    {
        // Fixed neutral query; the result is discarded and no source text is returned to the caller.
        await SearchCoreAsync(secret, "小红书", 1, token).ConfigureAwait(false);
    }

    internal static void EnsureCode(int code)
    {
        if (code == 0) return;
        throw new SocialSourceException(code switch
        {
            -100 => "SOCIAL_SESSION_EXPIRED",
            -101 => "SOCIAL_SESSION_INVALID",
            // 300011 is what the platform answers for a live session when the web client's request
            // signature (x-s / x-s-common / x-t / x-rap-param) is missing or wrong. A correctly signed
            // request carrying the same cookie returns results, so this is a signature refusal — not a
            // flagged account and not a stale cookie.
            300011 => "SOCIAL_SESSION_SIGNATURE_REQUIRED",
            -112 or 461 or 471 => "SOCIAL_RATE_LIMITED",
            _ => "SOCIAL_UPSTREAM_REFUSED",
        });
    }
}

/// <summary>
/// 小黑盒 authenticated search. The platform answers anonymous traffic with a captcha challenge but
/// accepts the web client's signed request carrying the user's own session cookie. The parameter set and
/// the <c>hkey</c> construction are part of the protocol: the older mobile-client set is answered with an
/// accepted envelope and an empty list instead of an error.
/// </summary>
internal sealed class XiaoheiheSessionConnection(ICredentialReader credentials, SocialHttpGateway gateway)
    : SocialSessionConnectionBase(SocialSourceSettings.Xiaoheihe, credentials, gateway)
{
    private const string ApiRoot = "https://api.xiaoheihe.cn";
    private const int DeviceWidth = 628;

    protected override async Task<SocialSourceResult> SearchCoreAsync(string secret, string query, int maxResults,
        CancellationToken token)
    {
        const string path = "/bbs/app/api/general/search/v1";
        var url = ApiRoot + path + "?" + XiaoheiheReader.WebQuery(
                XiaoheiheReader.HeyboxIdFromCookie(secret), XiaoheiheReader.WebVersionSearch)
            + "&q=" + Uri.EscapeDataString(query) + "&search_type=general&is_pull_down=0&offset=0&limit="
            + Math.Clamp(maxResults, 1, 20).ToString(CultureInfo.InvariantCulture)
            + "&dw=" + DeviceWidth.ToString(CultureInfo.InvariantCulture)
            + "&" + XiaoheiheReader.Sign(path);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Referer", "https://www.xiaoheihe.cn/");
        request.Headers.TryAddWithoutValidation("Origin", "https://www.xiaoheihe.cn");
        SocialHttpGateway.AddCommonHeaders(request, secret);
        var (_, body) = await SendAsync(request, token).ConfigureAwait(false);

        JsonDocument document;
        try { document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 64 }); }
        catch (JsonException) { throw new SocialSourceException("SOCIAL_RESPONSE_INVALID"); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new SocialSourceException("SOCIAL_RESPONSE_INVALID");
            var state = XiaoheiheReader.Text(root, "status");
            if (state is not "ok")
            {
                throw new SocialSourceException(state switch
                {
                    "show_captcha" => "SOCIAL_SESSION_INVALID",
                    "login" => "SOCIAL_SESSION_EXPIRED",
                    "expired" => "SOCIAL_UPSTREAM_REFUSED",
                    _ => "SOCIAL_UPSTREAM_REFUSED",
                });
            }
            var rows = new List<SocialSource>();
            if (root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object
                && result.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    if (rows.Count == maxResults) break;
                    if (MapSearchItem(item) is { } mapped) rows.Add(mapped);
                }
            }
            return Materialize(rows, "xiaoheihe_account_search", body);
        }
    }

    /// <summary>
    /// Search results mix posts, topics, mini apps and spacer rows; only <c>type == "link"</c> carries a
    /// post, and its fields live under <c>info</c>.
    /// </summary>
    private static SocialSource? MapSearchItem(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;
        if (XiaoheiheReader.Text(item, "type") is not "link") return null;
        if (!item.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Object) return null;
        var linkId = XiaoheiheReader.NumberOrString(info, "linkid")
            ?? XiaoheiheReader.NumberOrString(info, "link_id");
        if (linkId is null) return null;
        var rawTitle = XiaoheiheReader.Text(info, "title");
        if (string.IsNullOrWhiteSpace(rawTitle)) return null;

        var title = SocialSourceText.Clip(SocialSourceText.StripTags(rawTitle),
            SocialSourceText.MaxTitleRunes, out var titleTruncated);
        if (!SocialSourceText.SafeText(title, allowLineBreaks: false)) return null;

        var summary = SocialSourceText.StripTags(XiaoheiheReader.Text(info, "description") ?? string.Empty);
        if (!SocialSourceText.SafeText(summary, allowLineBreaks: true)) summary = string.Empty;
        var snippet = SocialSourceText.Clip(summary, SocialSourceText.MaxSummaryRunes, out var summaryTruncated);

        var postUrl = "https://www.xiaoheihe.cn/app/bbs/link/" + linkId;
        if (!SocialSourceText.SafeUrl(postUrl)) return null;

        string? author = null;
        if (info.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object)
            author = XiaoheiheReader.Text(user, "username") ?? XiaoheiheReader.Text(user, "nickname");
        return new SocialSource(SocialSourceSettings.Xiaoheihe, title, postUrl, snippet, author,
            XiaoheiheReader.UnixTime(info, "create_at"),
            XiaoheiheReader.NumberOrString(info, "comment_num") is { } comments
                ? comments + " comments" : null,
            titleTruncated, summaryTruncated);
    }

    protected override async Task ProbeAsync(string secret, CancellationToken token)
    {
        await SearchCoreAsync(secret, "小黑盒", 1, token).ConfigureAwait(false);
    }
}

/// <summary>
/// B站 authenticated search. A session is not required for anonymous search, but a signed-in session is
/// materially more stable than anonymous traffic (which answers 412 intermittently) and is the operator's
/// own account rather than an unauthenticated client. The anonymous reader stays available separately.
/// </summary>
internal sealed class BilibiliSessionConnection(ICredentialReader credentials, SocialHttpGateway gateway)
    : SocialSessionConnectionBase(SocialSourceSettings.Bilibili, credentials, gateway)
{
    private const string ApiRoot = "https://api.bilibili.com";
    private const string FingerUrl = ApiRoot + "/x/frontend/finger/spi";
    private const string NavUrl = ApiRoot + "/x/web-interface/nav";
    private string? _deviceCookie;

    protected override async Task<SocialSourceResult> SearchCoreAsync(string secret, string query, int maxResults,
        CancellationToken token)
    {
        var device = await DeviceAsync(token).ConfigureAwait(false);
        // Device identifiers and the account session are separate cookies; both travel together.
        var cookie = device.Length == 0 ? secret : device + "; " + secret;
        var url = ApiRoot + "/x/web-interface/search/type?search_type=video&page=1&keyword="
            + Uri.EscapeDataString(query);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
        SocialHttpGateway.AddCommonHeaders(request, cookie);
        var (_, body) = await SendAsync(request, token).ConfigureAwait(false);

        JsonDocument document;
        try { document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 64 }); }
        catch (JsonException) { throw new SocialSourceException("SOCIAL_RESPONSE_INVALID"); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("code", out var code)
                || !code.TryGetInt32(out var value))
                throw new SocialSourceException("SOCIAL_RESPONSE_INVALID");
            EnsureCode(value);
            var rows = new List<SocialSource>();
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in result.EnumerateArray())
                {
                    if (rows.Count == maxResults) break;
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var bvid = XiaoheiheReader.Text(item, "bvid");
                    if (bvid is null || !BilibiliReader.IsBvid(bvid)) continue;
                    if (MapRow(SocialSourceSettings.Bilibili, item) is not { } mapped) continue;
                    var author = XiaoheiheReader.Text(item, "author");
                    var plays = XiaoheiheReader.NumberOrString(item, "play");
                    var published = long.TryParse(XiaoheiheReader.NumberOrString(item, "pubdate"),
                        NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
                        && seconds is > 1_000_000_000 and < 4_000_000_000
                        ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime
                            .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
                        : null;
                    rows.Add(mapped with
                    {
                        Url = "https://www.bilibili.com/video/" + bvid,
                        Author = author,
                        PublishedAt = published,
                        Popularity = plays is null ? null : plays + " plays",
                        Summary = SocialSourceText.Clip(
                            SocialSourceText.StripTags(XiaoheiheReader.Text(item, "description") ?? string.Empty),
                            SocialSourceText.MaxSummaryRunes, out _),
                    });
                }
            }
            return Materialize(rows, "bilibili_account_search", body);
        }
    }

    /// <summary>
    /// The nav endpoint reports whether the presented session is live, so the connection test costs no
    /// search query and returns no source text.
    /// </summary>
    protected override async Task ProbeAsync(string secret, CancellationToken token)
    {
        var device = await DeviceAsync(token).ConfigureAwait(false);
        var cookie = device.Length == 0 ? secret : device + "; " + secret;
        using var request = new HttpRequestMessage(HttpMethod.Get, NavUrl);
        request.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
        SocialHttpGateway.AddCommonHeaders(request, cookie);
        var (_, body) = await SendAsync(request, token).ConfigureAwait(false);
        JsonDocument document;
        try { document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 32 }); }
        catch (JsonException) { throw new SocialSourceException("SOCIAL_RESPONSE_INVALID"); }
        using (document)
        {
            var root = document.RootElement;
            if (!root.TryGetProperty("code", out var code) || !code.TryGetInt32(out var value))
                throw new SocialSourceException("SOCIAL_RESPONSE_INVALID");
            if (value == 0)
            {
                var isLogin = root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                    && data.TryGetProperty("isLogin", out var login) && login.ValueKind == JsonValueKind.True;
                if (isLogin) return;
            }
            EnsureCode(value);
            // code 0 without isLogin means the session was not accepted as a login.
            throw new SocialSourceException("SOCIAL_SESSION_EXPIRED");
        }
    }

    /// <summary>
    /// Merges the anonymous device cookie with the account session. A pasted browser cookie usually
    /// already carries its own device identifiers, so ours are added only when absent; the account's
    /// values win and no duplicate cookie names are sent.
    /// </summary>
    internal static string Combine(string deviceCookie, string session)
    {
        if (deviceCookie.Length == 0) return session;
        var present = new HashSet<string>(
            session.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(pair => pair.Split('=', 2)[0].Trim()),
            StringComparer.OrdinalIgnoreCase);
        var missing = deviceCookie
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(pair => !present.Contains(pair.Split('=', 2)[0].Trim()))
            .ToArray();
        return missing.Length == 0 ? session : string.Join("; ", missing) + "; " + session;
    }
    /// <summary>Anonymous device cookie pair; cached per connection and refreshed only on reset.</summary>
    private async Task<string> DeviceAsync(CancellationToken token)
    {
        if (_deviceCookie is { } cached) return cached;
        using var request = new HttpRequestMessage(HttpMethod.Get, FingerUrl);
        request.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
        SocialHttpGateway.AddCommonHeaders(request, cookie: null);
        var (_, body) = await SendAsync(request, token).ConfigureAwait(false);
        _deviceCookie = BilibiliReader.ParseDeviceCookie(body) ?? string.Empty;
        return _deviceCookie;
    }

    private static void EnsureCode(int code)
    {
        if (code == 0) return;
        throw new SocialSourceException(code switch
        {
            -101 or -100 => "SOCIAL_SESSION_EXPIRED",
            -352 => "SOCIAL_SESSION_INVALID",
            -412 or -799 => "SOCIAL_RATE_LIMITED",
            -404 => "SOCIAL_NOT_FOUND",
            _ => "SOCIAL_UPSTREAM_REFUSED",
        });
    }
}