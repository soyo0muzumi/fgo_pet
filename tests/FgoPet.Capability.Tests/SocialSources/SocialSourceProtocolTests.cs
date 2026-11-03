using FgoPet.Plugin.SocialSources;
using FgoPet.Plugin.SocialSources.Platforms;
using Xunit;

namespace FgoPet.Capability.Tests.SocialSources;

/// <summary>
/// Wire-protocol tests for the platform readers. No network access: every case feeds recorded shapes
/// through the parsers or checks a locally computed signature against a fixed reference.
/// </summary>
public sealed class SocialSourceProtocolTests
{
    // Reference values published with the independent xiaoheihe-mcp implementation of the same public
    // algorithm (Insect-rippler/XIAOHEIHE_MCP, MIT), with the wall clock and randomness pinned. The live
    // service rejects the older digits-only construction with `非法请求`, so these vectors are load-bearing.
    [Theory]
    [InlineData("/bbs/app/feeds", 1750000000L, "D4DDFEAD7CBBD88223D35C5CF6D7C9AE",
        "hkey=U1V1I44&_time=1750000000&nonce=D4DDFEAD7CBBD88223D35C5CF6D7C9AE")]
    [InlineData("//bbs//app//feeds//", 1750000000L, "D4DDFEAD7CBBD88223D35C5CF6D7C9AE",
        "hkey=U1V1I44&_time=1750000000&nonce=D4DDFEAD7CBBD88223D35C5CF6D7C9AE")]
    [InlineData("/api/x", 1750000000L, "D4DDFEAD7CBBD88223D35C5CF6D7C9AE",
        "hkey=7VWPI35&_time=1750000000&nonce=D4DDFEAD7CBBD88223D35C5CF6D7C9AE")]
    public void Xiaohianghe_signature_matches_the_reference_implementation(string path, long timestamp,
        string nonce, string expected)
    {
        Assert.Equal(expected, XiaoheiheReader.Sign(path, timestamp, nonce));
    }

    [Fact]
    public void Xiaohianghe_signature_is_stable_for_a_pinned_clock_and_nonce()
    {
        var first = XiaoheiheReader.Sign("/bbs/app/link/tree", 1793500000L, "0123456789ABCDEF0123456789ABCDEF");
        var second = XiaoheiheReader.Sign("/bbs/app/link/tree", 1793500000L, "0123456789ABCDEF0123456789ABCDEF");
        Assert.Equal(first, second);
        Assert.StartsWith("hkey=", first, StringComparison.Ordinal);
        Assert.Contains("&_time=1793500000&", first, StringComparison.Ordinal);
    }

    [Fact]
    public void Xiaohianghe_web_client_parameters_are_fixed_and_carry_an_optional_account_id()
    {
        var anonymous = XiaoheiheReader.WebQuery(null, XiaoheiheReader.WebVersionRead);
        Assert.Contains("client_type=web", anonymous, StringComparison.Ordinal);
        Assert.Contains("version=999.0.4", anonymous, StringComparison.Ordinal);
        Assert.Contains("web_version=2.5", anonymous, StringComparison.Ordinal);
        Assert.Contains("x_app=heybox_website", anonymous, StringComparison.Ordinal);
        Assert.Contains("&heybox_id=&", anonymous, StringComparison.Ordinal);
        Assert.Contains("x_os_type=Windows", anonymous, StringComparison.Ordinal);

        var signedIn = XiaoheiheReader.WebQuery("84967100", XiaoheiheReader.WebVersionSearch);
        Assert.Contains("web_version=3.0", signedIn, StringComparison.Ordinal);
        Assert.Contains("heybox_id=84967100", signedIn, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("lang=zh-cn; heybox_id=84967100; pkey=abc", "84967100")]
    [InlineData("heybox_id=84967100", "84967100")]
    [InlineData("lang=zh-cn; pkey=abc", "")]
    [InlineData("heybox_id=not-a-number", "")]
    [InlineData("user_heybox_id=12345678; heybox_id=84967100", "84967100")]
    public void Xiaohianghe_reads_the_account_id_from_the_session_cookie(string cookie, string expected)
    {
        Assert.Equal(expected, XiaoheiheReader.HeyboxIdFromCookie(cookie));
    }

    [Theory]
    [InlineData("195656111", "195656111")]
    [InlineData("https://www.xiaoheihe.cn/app/bbs/link/195656111", "195656111")]
    [InlineData("https://api.xiaoheihe.cn/v3/bbs/app/api/web/share?link_id=195656111", "195656111")]
    public void Xiaoheihe_accepts_share_urls_and_bare_ids(string reference, string expected)
    {
        Assert.Equal(expected, XiaoheiheReader.LinkIdFrom(reference));
    }

    [Theory]
    [InlineData("https://evil.example/app/bbs/link/195656111")]
    [InlineData("file:///C:/temp/link/195656111")]
    [InlineData("javascript:alert(1)")]
    [InlineData("")]
    public void Xiaoheihe_rejects_foreign_references(string reference)
    {
        var error = Assert.Throws<SocialSourceException>(() => XiaoheiheReader.LinkIdFrom(reference));
        Assert.Equal("SOCIAL_REFERENCE_INVALID", error.Code);
    }

    [Theory]
    [InlineData("https://www.bilibili.com/video/BV1wExv6MEfT", "BV1wExv6MEfT")]
    [InlineData("BV1wExv6MEfT", "BV1wExv6MEfT")]
    [InlineData("https://www.bilibili.com/video/BV1wExv6MEfT/?p=1", "BV1wExv6MEfT")]
    public void Bilibili_accepts_video_urls_and_bare_bvid(string reference, string expected)
    {
        Assert.Equal(expected, BilibiliReader.BvidFrom(reference));
    }

    [Theory]
    [InlineData("https://evil.example/video/BV1wExv6MEfT")]
    [InlineData("file:///BV1wExv6MEfT")]
    [InlineData("https://www.bilibili.com/")]
    public void Bilibili_rejects_foreign_references(string reference)
    {
        var error = Assert.Throws<SocialSourceException>(() => BilibiliReader.BvidFrom(reference));
        Assert.Equal("SOCIAL_REFERENCE_INVALID", error.Code);
    }

    [Theory]
    [InlineData("6585468d000000000503bb1f", "6585468d000000000503bb1f")]
    [InlineData("https://www.xiaohongshu.com/explore/6585468d000000000503bb1f", "6585468d000000000503bb1f")]
    [InlineData("https://www.xiaohongshu.com/explore/6585468d000000000503bb1f?xsec_token=abc",
        "6585468d000000000503bb1f")]
    public void Xiaohongshu_accepts_note_urls_and_bare_ids(string reference, string expected)
    {
        Assert.Equal(expected, XiaohongshuReader.NoteIdFrom(reference));
    }

    [Theory]
    [InlineData("https://evil.example/explore/6585468d000000000503bb1f")]
    [InlineData("6585468D000000000503BB1F")]
    [InlineData("")]
    public void Xiaohongshu_rejects_foreign_references(string reference)
    {
        var error = Assert.Throws<SocialSourceException>(() => XiaohongshuReader.NoteIdFrom(reference));
        Assert.Equal("SOCIAL_REFERENCE_INVALID", error.Code);
    }

    [Fact]
    public void Xiaohongshu_parses_the_server_rendered_note_page()
    {
        // Shape taken from a public note page; `undefined` is a JS literal the parser must tolerate.
        const string html = """
            <html><body><script>window.__INITIAL_STATE__={"note":{"noteDetailMap":{
            "6585468d000000000503bb1f":{"note":{"noteId":"6585468d000000000503bb1f",
            "title":"护肤指南","desc":"正确顺序如下\n1 clean","type":"normal","time":1765976114000,
            "user":{"nickname":"省钱资讯站"},"interactInfo":{"likedCount":"172","collectedCount":"104",
            "commentCount":"20"},"queryTrendingInfo":undefined}}}}};</script></body></html>
            """;
        var note = XiaohongshuReader.ParseNoteCard(html);

        Assert.NotNull(note);
        Assert.Equal("护肤指南", note!.Title);
        Assert.Equal("https://www.xiaohongshu.com/explore/6585468d000000000503bb1f", note.Url);
        Assert.Equal("省钱资讯站", note.Author);
        Assert.StartsWith("2025-", note.PublishedAt, StringComparison.Ordinal);
        Assert.Contains("172 likes", note.Popularity, StringComparison.Ordinal);
    }

    [Fact]
    public void Xiaohongshu_returns_null_for_a_page_without_note_state()
    {
        Assert.Null(XiaohongshuReader.ParseNoteCard("<html><body>no state here</body></html>"));
    }

    [Fact]
    public void Bilibili_derives_the_wbi_mixing_key_from_the_nav_images()
    {
        const string body = """
            {"code":-101,"message":"账号未登录","data":{"wbi_img":{
            "img_url":"https://i0.hdslb.com/bfs/wbi/7cd084941338484aae1ad9425b84077c.png",
            "sub_url":"https://i0.hdslb.com/bfs/wbi/4932caff0ff746eab6f01bf08b70ac45.png"}}}
            """;
        Assert.Equal("ea1db124af3c7062474693fa704f4ff8", BilibiliReader.ParseMixinKey(body));
    }

    [Fact]
    public void Bilibili_reads_the_anonymous_device_cookie_pair()
    {
        const string body = """{"code":0,"data":{"b_3":"ABC-DEF","b_4":"GHI-JKL"},"message":"ok"}""";
        Assert.Equal("buvid3=ABC-DEF; buvid4=GHI-JKL", BilibiliReader.ParseDeviceCookie(body));
    }

    [Fact]
    public void Bilibili_rejects_a_fingerprint_response_without_a_device_id()
    {
        Assert.Null(BilibiliReader.ParseDeviceCookie("""{"code":0,"data":{}}"""));
        Assert.Null(BilibiliReader.ParseDeviceCookie("not json"));
    }

    [Fact]
    public void Bilibili_wbi_signature_is_deterministic_for_a_pinned_clock()
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["oid"] = "117528911152706",
            ["type"] = "1",
            ["wts"] = "1",
        };
        var first = BilibiliReader.SignWbi(parameters, "ea1db124af3c7062474693fa704f4ff8", 1700000000L);
        var second = BilibiliReader.SignWbi(parameters, "ea1db124af3c7062474693fa704f4ff8", 1700000000L);
        Assert.Equal(first, second);
        Assert.Contains("wts=1700000000", first, StringComparison.Ordinal);
        Assert.Matches(@"w_rid=[0-9a-f]{32}$", first);
    }
}
