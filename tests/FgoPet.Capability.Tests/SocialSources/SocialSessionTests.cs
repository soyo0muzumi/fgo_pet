using System.Text.Json;
using FgoPet.Core.Secrets;
using FgoPet.Extensibility;
using FgoPet.Plugin.SocialSources;
using Xunit;

namespace FgoPet.Capability.Tests.SocialSources;

/// <summary>
/// The optional authenticated session: grant handling, lazy secret resolution, connection fencing and
/// the rule that the secret never appears in a payload or diagnostic. No network and no real credential.
/// </summary>
public sealed class SocialSessionTests
{
    private static readonly ToolScope Scope = new("conversation", "role", "project");

    private static string TempDirectory() =>
        Path.Combine(Path.GetTempPath(), "fgo-social-session-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Session_tools_are_absent_when_the_host_supplies_no_session_storage()
    {
        using var plugin = Create(new FixtureCredentials("web_session=abc"), withStores: false);
        var names = plugin.Contributions.Tools.Select(tool => tool.Descriptor.Name).ToArray();

        Assert.DoesNotContain("social.bilibili.search", names);
        Assert.DoesNotContain("social.xiaohongshu.search", names);
        Assert.DoesNotContain("social.xiaoheihe.search", names);
        Assert.Equal(3, names.Length);
    }

    [Fact]
    public async Task Session_tools_are_contributed_and_default_to_unavailable()
    {
        using var plugin = Create(new FixtureCredentials("web_session=abc"), withStores: true);
        await plugin.StartAsync(default);
        var tools = plugin.Contributions.Tools.ToDictionary(tool => tool.Descriptor.Name, StringComparer.Ordinal);

        Assert.Contains("social.bilibili.search", tools.Keys);
        Assert.Contains("social.xiaohongshu.search", tools.Keys);
        Assert.Contains("social.xiaoheihe.search", tools.Keys);
        foreach (var name in new[] { "social.bilibili.search", "social.xiaohongshu.search", "social.xiaoheihe.search" })
        {
            Assert.Equal(ToolEffect.ReadOnly, tools[name].Descriptor.Effect);
            var availability = ((IToolAvailabilityProvider)tools[name]).GetAvailability();
            Assert.False(availability.IsAvailable);
            Assert.Equal("SOCIAL_SESSION_NOT_CONFIGURED", availability.ErrorCode);
        }
    }

    [Fact]
    public void Session_snapshot_never_reads_the_secret_and_reports_no_connection()
    {
        var directory = TempDirectory();
        try
        {
            var credentials = new FixtureCredentials("web_session=abc");
            var stores = new SocialSourceSettingsStores(directory);
            using var plugin = Create(credentials, stores: stores);
            var snapshot = plugin.GetSessionSnapshot(SocialSourceSettings.Xiaohongshu);
            Assert.False(snapshot.Configured);
            Assert.False(snapshot.Enabled);
            Assert.False(snapshot.Connected);
            Assert.Equal("SOCIAL_SESSION_NOT_CONFIGURED", snapshot.ErrorCode);
            Assert.Equal(0, credentials.Reads);
        }
        finally { Clean(directory); }
    }

    [Fact]
    public async Task Enabling_a_session_grant_rotates_and_does_not_create_the_secret()
    {
        var directory = TempDirectory();
        try
        {
            var credentials = new FixtureCredentials("web_session=abc");
            var stores = new SocialSourceSettingsStores(directory);
            using var plugin = Create(credentials, stores: stores);
            await plugin.StartAsync(default);

            var enabled = await plugin.SetSessionEnabledAsync(SocialSourceSettings.Xiaoheihe, true, default);
            Assert.True(enabled.Configured);
            Assert.True(enabled.Enabled);
            Assert.Null(enabled.ErrorCode);

            var first = stores.XiaoheiheSession.Load();
            Assert.NotNull(first);
            Assert.Equal(SocialSourceSettings.Xiaoheihe, first!.Platform);

            await plugin.SetSessionEnabledAsync(SocialSourceSettings.Xiaoheihe, true, default);
            var second = stores.XiaoheiheSession.Load();
            Assert.NotNull(second);
            Assert.NotEqual(first.AuthorizationId, second!.AuthorizationId);
            Assert.True(second.AuthorizationVersion > first.AuthorizationVersion);
            // The grant file itself never carries the session.
            Assert.DoesNotContain("web_session", File.ReadAllText(stores.XiaoheiheSession.Path), StringComparison.Ordinal);
        }
        finally { Clean(directory); }
    }

    [Fact]
    public async Task Missing_secret_is_reported_as_a_distinct_error_and_never_retried()
    {
        var directory = TempDirectory();
        try
        {
            var credentials = new FixtureCredentials(null);
            var stores = new SocialSourceSettingsStores(directory);
            using var plugin = Create(credentials, stores: stores);
            await plugin.StartAsync(default);
            await plugin.SetSessionEnabledAsync(SocialSourceSettings.Xiaoheihe, true, default);
            var tool = plugin.Contributions.Tools.Single(item =>
                item.Descriptor.Name == "social.xiaoheihe.search");

            var result = await tool.InvokeAsync(new(Scope, Arguments(new { query = "fixture" })), default);

            Assert.False(result.Success);
            Assert.Equal("SOCIAL_SESSION_MISSING", result.ErrorCode);
            Assert.Equal("{}", result.Payload.GetRawText());
        }
        finally { Clean(directory); }
    }

    [Fact]
    public async Task An_expired_session_is_reported_and_the_cached_secret_is_dropped()
    {
        var directory = TempDirectory();
        try
        {
            var credentials = new FixtureCredentials("web_session=expired");
            var stores = new SocialSourceSettingsStores(directory);
            var handler = new RoutingHandler
            {
                Search = _ => (System.Net.HttpStatusCode.OK,
                    """{"code":-100,"success":false,"msg":"登录已过期"}"""),
            };
            using var plugin = Create(credentials, stores: stores, handler: handler);
            await plugin.StartAsync(default);
            await plugin.SetSessionEnabledAsync(SocialSourceSettings.Xiaohongshu, true, default);
            var tool = plugin.Contributions.Tools.Single(item =>
                item.Descriptor.Name == "social.xiaohongshu.search");

            var result = await tool.InvokeAsync(new(Scope, Arguments(new { query = "fixture" })), default);

            Assert.False(result.Success);
            Assert.Equal("SOCIAL_SESSION_EXPIRED", result.ErrorCode);
            // The connection must not keep a rejected session around for the next attempt.
            Assert.False(plugin.GetSessionSnapshot(SocialSourceSettings.Xiaohongshu).Connected);
        }
        finally { Clean(directory); }
    }

    [Fact]
    public async Task A_successful_search_returns_platform_urls_and_provenance()
    {
        var directory = TempDirectory();
        try
        {
            var credentials = new FixtureCredentials("web_session=valid");
            var stores = new SocialSourceSettingsStores(directory);
            var handler = new RoutingHandler
            {
                Search = _ => (System.Net.HttpStatusCode.OK, """
                    {"code":0,"success":true,"data":{"items":[
                    {"id":"69992f27000000001a032819","xsec_token":"abc",
                     "note_card":{"display_title":"护肤指南","desc":"正确顺序",
                      "user":{"nickname":"作者"},"interact_info":{"liked_count":"172"}}},
                    {"id":"not-a-note-id","note_card":{"display_title":"invalid"}}]}}
                    """),
            };
            using var plugin = Create(credentials, stores: stores, handler: handler);
            await plugin.StartAsync(default);
            await plugin.SetSessionEnabledAsync(SocialSourceSettings.Xiaohongshu, true, default);
            var tool = plugin.Contributions.Tools.Single(item =>
                item.Descriptor.Name == "social.xiaohongshu.search");

            var result = await tool.InvokeAsync(new(Scope, Arguments(new { query = "护肤" })), default);

            Assert.True(result.Success, result.ErrorCode);
            Assert.Equal("xiaohongshu_account_search", result.Payload.GetProperty("coverage").GetString());
            var source = Assert.Single(result.Payload.GetProperty("results").EnumerateArray());
            Assert.Equal("护肤指南", source.GetProperty("title").GetString());
            Assert.Equal("https://www.xiaohongshu.com/explore/69992f27000000001a032819?xsec_token=abc",
                source.GetProperty("url").GetString());
            Assert.Equal("作者", source.GetProperty("author").GetString());
            Assert.Matches("^[A-F0-9]{64}$", result.Payload.GetProperty("sourceVersion").GetString()!);
            // The secret must never surface in a model payload.
            Assert.DoesNotContain("web_session", result.Payload.GetRawText(), StringComparison.Ordinal);
        }
        finally { Clean(directory); }
    }

    [Fact]
    public async Task Session_search_rejects_extra_or_malformed_arguments_before_reading_the_secret()
    {
        var directory = TempDirectory();
        try
        {
            var credentials = new FixtureCredentials("web_session=valid");
            var stores = new SocialSourceSettingsStores(directory);
            using var plugin = Create(credentials, stores: stores);
            await plugin.StartAsync(default);
            await plugin.SetSessionEnabledAsync(SocialSourceSettings.Xiaoheihe, true, default);
            var tool = plugin.Contributions.Tools.Single(item =>
                item.Descriptor.Name == "social.xiaoheihe.search");

            foreach (var bad in new[]
            {
                """{"query":"a","reference":"b"}""",
                """{"query":"a","maxResults":21}""",
                """{"query":"a","platform":"x"}""",
                """{"query":""}""",
                """{}""",
            })
            {
                using var document = JsonDocument.Parse(bad);
                var result = await tool.InvokeAsync(new(Scope, document.RootElement.Clone()), default);
                Assert.False(result.Success);
                Assert.Equal("TOOL_INVALID_ARGUMENTS", result.ErrorCode);
            }
            Assert.True(credentials.Reads <= 1);
        }
        finally { Clean(directory); }
    }

    [Fact]
    public async Task A_xiaoheihe_search_keeps_only_post_rows_and_sends_the_web_client_request()
    {
        var directory = TempDirectory();
        try
        {
            var credentials = new FixtureCredentials("lang=zh-cn; heybox_id=84967100; pkey=abc");
            var stores = new SocialSourceSettingsStores(directory);
            string? requestUri = null;
            var handler = new RoutingHandler
            {
                Search = request =>
                {
                    requestUri = request.RequestUri!.ToString();
                    return (System.Net.HttpStatusCode.OK, """
                        {"msg":"","status":"ok","result":{"items":[
                        {"type":"space","info":{}},
                        {"type":"link","info":{"linkid":193485683,"title":"<em>原神</em> 卡池",
                         "description":"正文","user":{"username":"作者","userid":1},
                         "comment_num":37,"create_at":1792726179}},
                        {"type":"topic","info":{"name":"原神"}}]}}
                        """);
                },
            };
            using var plugin = Create(credentials, stores: stores, handler: handler);
            await plugin.StartAsync(default);
            await plugin.SetSessionEnabledAsync(SocialSourceSettings.Xiaoheihe, true, default);
            var tool = plugin.Contributions.Tools.Single(item =>
                item.Descriptor.Name == "social.xiaoheihe.search");

            var result = await tool.InvokeAsync(new(Scope, Arguments(new { query = "原神" })), default);

            Assert.True(result.Success, result.ErrorCode);
            var source = Assert.Single(result.Payload.GetProperty("results").EnumerateArray());
            Assert.Equal("原神 卡池", source.GetProperty("title").GetString());
            Assert.Equal("https://www.xiaoheihe.cn/app/bbs/link/193485683",
                source.GetProperty("url").GetString());
            Assert.Equal("作者", source.GetProperty("author").GetString());
            Assert.Equal("37 comments", source.GetProperty("popularity").GetString());
            Assert.NotNull(requestUri);
            Assert.Contains("search_type=general", requestUri, StringComparison.Ordinal);
            Assert.Contains("web_version=3.0", requestUri, StringComparison.Ordinal);
            Assert.Contains("heybox_id=84967100", requestUri, StringComparison.Ordinal);
            Assert.Contains("hkey=", requestUri, StringComparison.Ordinal);
            // The account id travels in the request only; it never enters a model payload.
            Assert.DoesNotContain("heybox_id", result.Payload.GetRawText(), StringComparison.Ordinal);
        }
        finally { Clean(directory); }
    }

    private static JsonElement Arguments(object value) => JsonSerializer.SerializeToElement(value);

    private static SocialSourcesPlugin Create(FixtureCredentials credentials,
        bool withStores = true, SocialSourceSettingsStores? stores = null, RoutingHandler? handler = null)
    {
        stores ??= withStores
            ? new SocialSourceSettingsStores(Path.Combine(Path.GetTempPath(),
                "fgo-social-session-auto-" + Guid.NewGuid().ToString("N")))
            : null!;
        return new SocialSourcesPlugin(
            () => null, () => null, () => null,
            () => "SOCIAL_NOT_CONFIGURED",
            _ => true,
            new SocialHttpGateway(handler ?? new RoutingHandler()),
            diagnostics: null,
            stores: withStores ? stores : null,
            credentials: withStores ? credentials : null);
    }

    private static void Clean(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch (IOException) { }
    }

    private sealed class FixtureCredentials(string? secret) : ICredentialReader
    {
        public int Reads { get; private set; }
        public Task<string?> ReadAsync(string target, CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult(secret);
        }
    }

    private sealed class RoutingHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, (System.Net.HttpStatusCode Status, string Body)>? Search { get; init; }
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            var (status, body) = Search?.Invoke(request) ?? (System.Net.HttpStatusCode.OK, """{"code":0}""");
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}

/// <summary>Platform refusal codes that must not be mistaken for a stale session.</summary>
public sealed class SocialSessionRefusalTests
{
    [Fact]
    public void A_signature_refusal_gets_its_own_code_and_is_not_treated_as_expired()
    {
        // 300011 ("当前账号存在异常") is what 小红书 answers for a live session when the web client's
        // request signature is missing; a correctly signed request from the same cookie returns results.
        var error = Assert.Throws<FgoPet.Plugin.SocialSources.SocialSourceException>(
            () => FgoPet.Plugin.SocialSources.XiaohongshuSessionConnection.EnsureCode(300011));
        Assert.Equal("SOCIAL_SESSION_SIGNATURE_REQUIRED", error.Code);
    }

    [Theory]
    [InlineData(-100, "SOCIAL_SESSION_EXPIRED")]
    [InlineData(-101, "SOCIAL_SESSION_INVALID")]
    [InlineData(-112, "SOCIAL_RATE_LIMITED")]
    [InlineData(461, "SOCIAL_RATE_LIMITED")]
    [InlineData(-3, "SOCIAL_UPSTREAM_REFUSED")]
    public void Other_refusals_map_to_their_own_codes(int code, string expected)
    {
        var error = Assert.Throws<FgoPet.Plugin.SocialSources.SocialSourceException>(
            () => FgoPet.Plugin.SocialSources.XiaohongshuSessionConnection.EnsureCode(code));
        Assert.Equal(expected, error.Code);
    }

    [Fact]
    public void Success_code_does_not_throw()
        => FgoPet.Plugin.SocialSources.XiaohongshuSessionConnection.EnsureCode(0);
}