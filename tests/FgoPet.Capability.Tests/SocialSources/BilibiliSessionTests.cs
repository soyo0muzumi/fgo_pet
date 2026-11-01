using System.Text.Json;
using FgoPet.Core.Secrets;
using FgoPet.Extensibility;
using FgoPet.Plugin.SocialSources;
using Xunit;

namespace FgoPet.Capability.Tests.SocialSources;

/// <summary>
/// B站 authenticated search: the device cookie must still accompany the account session, and the
/// connection test must use the login-status endpoint rather than spending a search query.
/// </summary>
public sealed class BilibiliSessionTests
{
    private static readonly ToolScope Scope = new("conversation", "role", "project");

    private static string TempDirectory() =>
        Path.Combine(Path.GetTempPath(), "fgo-bili-session-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Search_sends_device_cookie_together_with_the_account_session()
    {
        var directory = TempDirectory();
        try
        {
            var handler = new BilibiliHandler();
            var stores = new SocialSourceSettingsStores(directory);
            using var plugin = Create(new FixtureCredentials("SESSDATA=account-session"), stores, handler);
            await plugin.StartAsync(default);
            await plugin.SetSessionEnabledAsync(SocialSourceSettings.Bilibili, true, default);
            var tool = plugin.Contributions.Tools.Single(item =>
                item.Descriptor.Name == "social.bilibili.search");

            var result = await tool.InvokeAsync(new(Scope, Arguments(new { query = "玛修", maxResults = 2 })), default);

            Assert.True(result.Success, result.ErrorCode);
            Assert.Equal("bilibili_account_search", result.Payload.GetProperty("coverage").GetString());
            var source = Assert.Single(result.Payload.GetProperty("results").EnumerateArray());
            Assert.Equal("https://www.bilibili.com/video/BV1wExv6MEfT", source.GetProperty("url").GetString());
            Assert.Equal("fixture title", source.GetProperty("title").GetString());
            Assert.Equal("fixture-author", source.GetProperty("author").GetString());
            Assert.Equal("1234 plays", source.GetProperty("popularity").GetString());
            // Both cookie kinds must travel: the anonymous device id and the account session.
            var cookie = Assert.Single(handler.SearchCookies);
            Assert.Contains("buvid3=", cookie, StringComparison.Ordinal);
            Assert.Contains("SESSDATA=account-session", cookie, StringComparison.Ordinal);
            Assert.DoesNotContain("account-session", result.Payload.GetRawText(), StringComparison.Ordinal);
        }
        finally { Clean(directory); }
    }

    [Fact]
    public async Task Connection_test_uses_the_login_status_endpoint_and_sends_no_search_query()
    {
        var directory = TempDirectory();
        try
        {
            var handler = new BilibiliHandler { NavIsLogin = true };
            var stores = new SocialSourceSettingsStores(directory);
            using var plugin = Create(new FixtureCredentials("SESSDATA=account-session"), stores, handler);
            await plugin.StartAsync(default);
            await plugin.SetSessionEnabledAsync(SocialSourceSettings.Bilibili, true, default);

            var availability = await plugin.CheckSessionAsync(SocialSourceSettings.Bilibili, default);

            Assert.True(availability.IsAvailable, availability.ErrorCode);
            Assert.True(handler.NavCalls >= 1);
            Assert.Equal(0, handler.SearchCalls);
        }
        finally { Clean(directory); }
    }

    [Fact]
    public async Task Connection_test_reports_an_expired_session_when_login_status_is_false()
    {
        var directory = TempDirectory();
        try
        {
            var handler = new BilibiliHandler { NavIsLogin = false };
            var stores = new SocialSourceSettingsStores(directory);
            using var plugin = Create(new FixtureCredentials("SESSDATA=stale"), stores, handler);
            await plugin.StartAsync(default);
            await plugin.SetSessionEnabledAsync(SocialSourceSettings.Bilibili, true, default);

            var availability = await plugin.CheckSessionAsync(SocialSourceSettings.Bilibili, default);

            Assert.False(availability.IsAvailable);
            Assert.Equal("SOCIAL_SESSION_EXPIRED", availability.ErrorCode);
            Assert.False(plugin.GetSessionSnapshot(SocialSourceSettings.Bilibili).Connected);
        }
        finally { Clean(directory); }
    }

    [Fact]
    public async Task Anonymous_reader_is_unaffected_by_the_session_grant()
    {
        var directory = TempDirectory();
        try
        {
            var handler = new BilibiliHandler();
            var stores = new SocialSourceSettingsStores(directory);
            using var plugin = Create(new FixtureCredentials("SESSDATA=account-session"), stores, handler);
            await plugin.StartAsync(default);
            await plugin.SetSessionEnabledAsync(SocialSourceSettings.Bilibili, true, default);

            var anonymous = plugin.Contributions.Tools.Single(item =>
                item.Descriptor.Name == "social.bilibili");
            // The anonymous tool still requires its own grant, which is absent here.
            var availability = ((IToolAvailabilityProvider)anonymous).GetAvailability();

            Assert.False(availability.IsAvailable);
            Assert.Equal("SOCIAL_NOT_CONFIGURED", availability.ErrorCode);
        }
        finally { Clean(directory); }
    }

    private static JsonElement Arguments(object value) => JsonSerializer.SerializeToElement(value);

    private static SocialSourcesPlugin Create(FixtureCredentials credentials,
        SocialSourceSettingsStores stores, HttpMessageHandler handler) =>
        new(() => null, () => null, () => null,
            () => "SOCIAL_NOT_CONFIGURED", _ => true,
            new SocialHttpGateway(handler), diagnostics: null, stores: stores, credentials: credentials);

    private static void Clean(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch (IOException) { }
    }

    private sealed class FixtureCredentials(string? secret) : ICredentialReader
    {
        public Task<string?> ReadAsync(string target, CancellationToken cancellationToken) =>
            Task.FromResult(secret);
    }

    /// <summary>Answers the fingerprint, nav and search endpoints by path; records the cookies it saw.</summary>
    private sealed class BilibiliHandler : HttpMessageHandler
    {
        public string Search { get; init; } = "fixture";
        public bool NavIsLogin { get; init; } = true;
        public int NavCalls { get; private set; }
        public int SearchCalls { get; private set; }
        public List<string> SearchCookies { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var cookies = request.Headers.TryGetValues("Cookie", out var values)
                ? string.Join("; ", values) : string.Empty;
            string body;
            switch (path)
            {
                case "/x/frontend/finger/spi":
                    body = """{"code":0,"data":{"b_3":"device-three","b_4":"device-four"},"message":"ok"}""";
                    break;
                case "/x/web-interface/nav":
                    NavCalls++;
                    body = NavIsLogin
                        ? """{"code":0,"data":{"isLogin":true,"uname":"fixture"},"message":"0"}"""
                        : """{"code":0,"data":{"isLogin":false},"message":"0"}""";
                    break;
                case "/x/web-interface/search/type":
                    SearchCalls++;
                    SearchCookies.Add(cookies);
                    body = """
                        {"code":0,"message":"OK","data":{"result":[
                        {"bvid":"BV1wExv6MEfT","title":"fixture title","author":"fixture-author",
                         "play":1234,"pubdate":1793500000,"description":"fixture description"}]}}
                        """;
                    break;
                default:
                    body = """{"code":0}""";
                    break;
            }
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}

/// <summary>Cookie composition: the account's own device identifiers win, ours fill gaps only.</summary>
public sealed class BilibiliCookieMergeTests
{
    [Fact]
    public void Device_cookies_are_added_when_the_session_has_none()
    {
        var merged = FgoPet.Plugin.SocialSources.BilibiliSessionConnection.Combine(
            "buvid3=device; buvid4=device4", "SESSDATA=account");
        Assert.Equal("buvid3=device; buvid4=device4; SESSDATA=account", merged);
    }

    [Fact]
    public void Session_device_cookies_are_kept_and_ours_are_dropped()
    {
        // A pasted browser cookie carries its own buvid; sending ours too would duplicate the name.
        var merged = FgoPet.Plugin.SocialSources.BilibiliSessionConnection.Combine(
            "buvid3=device; buvid4=device4", "buvid3=fromBrowser; SESSDATA=account");
        Assert.Equal("buvid4=device4; buvid3=fromBrowser; SESSDATA=account", merged);
        Assert.Equal(1, merged.Split(';').Count(part => part.TrimStart().StartsWith("buvid3=", StringComparison.Ordinal)));
    }

    [Fact]
    public void Component_matching_is_case_insensitive_and_session_only_stays_unchanged()
    {
        var merged = FgoPet.Plugin.SocialSources.BilibiliSessionConnection.Combine(
            "BUVID3=device", "buvid3=fromBrowser; SESSDATA=account");
        Assert.Equal("buvid3=fromBrowser; SESSDATA=account", merged);
        Assert.Equal("SESSDATA=account",
            FgoPet.Plugin.SocialSources.BilibiliSessionConnection.Combine(string.Empty, "SESSDATA=account"));
    }
}