using System.Text.Json;
using FgoPet.Extensibility;
using FgoPet.Plugin.SocialSources;
using FgoPet.Plugin.SocialSources.Platforms;
using Xunit;

namespace FgoPet.Capability.Tests.SocialSources;

/// <summary>
/// Tool-contract tests: argument rejection, grant gating, scope enforcement and payload hygiene.
/// Network access is replaced by a recording handler, so every assertion is local and deterministic.
/// </summary>
public sealed class SocialSourcesPluginTests
{
    private static readonly ToolScope Scope = new("conversation", "role", "project");

    private static SocialSourceSettings Grant(string platform) =>
        new(true, "8dc9a81a6aac4720a06fe4b936d69e24", 1, platform);

    [Fact]
    public void One_tool_is_contributed_per_platform_and_all_are_read_only()
    {
        using var plugin = Create();
        var tools = plugin.Contributions.Tools;

        Assert.Equal(
            new[] { "social.bilibili", "social.xiaoheihe", "social.xiaohongshu" },
            tools.Select(tool => tool.Descriptor.Name).OrderBy(name => name, StringComparer.Ordinal));
        Assert.All(tools, tool => Assert.Equal(ToolEffect.ReadOnly, tool.Descriptor.Effect));
        // A model argument can never select a platform, endpoint or credential.
        Assert.All(tools, tool => Assert.Contains("\"additionalProperties\":false",
            tool.Descriptor.Parameters.GetRawText()));
    }

    [Fact]
    public async Task No_grant_means_no_dispatch()
    {
        var handler = new RecordingHandler();
        using var plugin = Create(handler: handler, grants: _ => null);
        await plugin.StartAsync(default);

        foreach (var tool in plugin.Contributions.Tools)
        {
            var result = await tool.InvokeAsync(new(Scope, Arguments(new { query = "fixture" })), default);
            Assert.False(result.Success);
            Assert.Equal("SOCIAL_NOT_CONFIGURED", result.ErrorCode);
        }
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task A_grant_for_one_platform_leaves_the_others_unavailable()
    {
        var handler = new RecordingHandler();
        using var plugin = Create(handler: handler,
            grants: platform => platform == SocialSourceSettings.Xiaoheihe ? Grant(platform) : null);
        await plugin.StartAsync(default);
        var tools = plugin.Contributions.Tools.ToDictionary(tool => tool.Descriptor.Name, StringComparer.Ordinal);

        Assert.False(((IToolAvailabilityProvider)tools["social.bilibili"]).GetAvailability().IsAvailable);
        Assert.False(((IToolAvailabilityProvider)tools["social.xiaohongshu"]).GetAvailability().IsAvailable);
        Assert.True(((IToolAvailabilityProvider)tools["social.xiaoheihe"]).GetAvailability().IsAvailable);

        var denied = await tools["social.bilibili"].InvokeAsync(new(Scope, Arguments(new { query = "x" })), default);
        Assert.Equal("SOCIAL_NOT_CONFIGURED", denied.ErrorCode);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    // both modes at once
    [InlineData("""{"query":"a","reference":"b"}""")]
    // neither mode
    [InlineData("""{}""")]
    // extra field
    [InlineData("""{"query":"a","platform":"bilibili"}""")]
    // wrong types
    [InlineData("""{"query":1}""")]
    [InlineData("""{"query":""}""")]
    [InlineData("""{"query":"   "}""")]
    [InlineData("""{"query":"a","maxResults":0}""")]
    [InlineData("""{"query":"a","maxResults":11}""")]
    [InlineData("""{"query":"a","maxResults":1.5}""")]
    // maxResults is meaningless when reading a single reference
    [InlineData("""{"reference":"https://www.bilibili.com/video/BV1wExv6MEfT","maxResults":3}""")]
    // duplicate keys
    [InlineData("""{"query":"a","query":"b"}""")]
    public async Task Invalid_or_ambiguous_arguments_never_reach_the_network(string argumentsJson)
    {
        var handler = new RecordingHandler();
        using var plugin = Create(handler: handler, grants: Grant);
        await plugin.StartAsync(default);
        var tool = plugin.Contributions.Tools.Single(item => item.Descriptor.Name == "social.bilibili");

        using var document = JsonDocument.Parse(argumentsJson);
        var result = await tool.InvokeAsync(new(Scope, document.RootElement.Clone()), default);

        Assert.False(result.Success);
        Assert.Equal("TOOL_INVALID_ARGUMENTS", result.ErrorCode);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Scope_that_is_no_longer_current_denies_the_read()
    {
        var handler = new RecordingHandler();
        using var plugin = Create(handler: handler, grants: Grant, scopeCurrent: _ => false);
        await plugin.StartAsync(default);
        var tool = plugin.Contributions.Tools.Single(item => item.Descriptor.Name == "social.bilibili");

        var result = await tool.InvokeAsync(new(Scope, Arguments(new { query = "fixture" })), default);

        Assert.False(result.Success);
        Assert.Equal("SOCIAL_SCOPE_DENIED", result.ErrorCode);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Stopped_plugin_denies_new_dispatch()
    {
        using var plugin = Create(grants: Grant);
        await plugin.StartAsync(default);
        await plugin.StopAsync(default);
        var tool = plugin.Contributions.Tools.Single(item => item.Descriptor.Name == "social.bilibili");

        var result = await tool.InvokeAsync(new(Scope, Arguments(new { query = "fixture" })), default);

        Assert.Equal("SOCIAL_STOPPED", result.ErrorCode);
    }

    [Fact]
    public async Task Xiaohongshu_search_uses_index_discovery_instead_of_a_platform_search_api()
    {
        // The platform refuses anonymous keyword search, so discovery runs through the index and the
        // returned rows must be real note URLs, never a silent empty page.
        const string index = """
            <div class="result"><a class="result__a" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Fwww.xiaohongshu.com%2Fexplore%2F69992f27000000001a032819">护肤指南 - 小红书</a><a class="result__snippet">正确的护肤顺序</a></div>
            <div class="result"><a class="result__a" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Fevil.example%2Fexplore%2F69992f27000000001a032819">不可信来源</a><a class="result__snippet">x</a></div>
            """;
        var handler = new RecordingHandler { Respond = _ => (System.Net.HttpStatusCode.OK, index) };
        using var plugin = Create(handler: handler, grants: Grant);
        await plugin.StartAsync(default);
        var tool = plugin.Contributions.Tools.Single(item => item.Descriptor.Name == "social.xiaohongshu");

        var result = await tool.InvokeAsync(new(Scope, Arguments(new { query = "护肤" })), default);

        Assert.True(result.Success, result.ErrorCode);
        Assert.Equal("search_index_discovery", result.Payload.GetProperty("coverage").GetString());
        var source = Assert.Single(result.Payload.GetProperty("results").EnumerateArray());
        Assert.Equal("https://www.xiaohongshu.com/explore/69992f27000000001a032819",
            source.GetProperty("url").GetString());
        // A result pointing outside the platform is dropped rather than returned to the model.
        Assert.DoesNotContain("evil.example", result.Payload.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Xiaoheihe_search_uses_index_discovery_for_post_urls()
    {
        const string index = """
            <a class="result__a" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Fwww.xiaoheihe.cn%2Fapp%2Fbbs%2Flink%2F188978643">影之刃零，启动!!! - 小黑盒</a></td><td><a class="result__snippet">实测</a>
            """;
        var handler = new RecordingHandler { Respond = _ => (System.Net.HttpStatusCode.OK, index) };
        using var plugin = Create(handler: handler, grants: Grant);
        await plugin.StartAsync(default);
        var tool = plugin.Contributions.Tools.Single(item => item.Descriptor.Name == "social.xiaoheihe");

        var result = await tool.InvokeAsync(new(Scope, Arguments(new { query = "影之刃零" })), default);

        Assert.True(result.Success, result.ErrorCode);
        var source = Assert.Single(result.Payload.GetProperty("results").EnumerateArray());
        Assert.Equal("https://www.xiaoheihe.cn/app/bbs/link/188978643", source.GetProperty("url").GetString());
    }

    [Fact]
    public async Task Search_index_challenge_is_reported_as_rate_limited_not_as_no_results()
    {
        var handler = new RecordingHandler
        {
            Respond = _ => (System.Net.HttpStatusCode.OK, "<html><div id=\"challenge-form\"></div></html>"),
        };
        using var plugin = Create(handler: handler, grants: Grant);
        await plugin.StartAsync(default);
        var tool = plugin.Contributions.Tools.Single(item => item.Descriptor.Name == "social.xiaohongshu");

        var result = await tool.InvokeAsync(new(Scope, Arguments(new { query = "fixture" })), default);

        Assert.False(result.Success);
        Assert.Equal("SOCIAL_RATE_LIMITED", result.ErrorCode);
    }

    [Fact]
    public async Task Upstream_refusal_is_reported_as_a_safe_code_without_the_body()
    {
        var handler = new RecordingHandler
        {
            Respond = _ => (System.Net.HttpStatusCode.OK,
                """{"status":"show_captcha","result":{"private":"do-not-leak"}}"""),
        };
        using var plugin = Create(handler: handler, grants: Grant);
        await plugin.StartAsync(default);
        var tool = plugin.Contributions.Tools.Single(item => item.Descriptor.Name == "social.xiaoheihe");

        var result = await tool.InvokeAsync(new(Scope, Arguments(
            new { reference = "https://www.xiaoheihe.cn/app/bbs/link/195656111" })), default);

        Assert.False(result.Success);
        Assert.Equal("SOCIAL_CAPTCHA_REQUIRED", result.ErrorCode);
        Assert.Equal("{}", result.Payload.GetRawText());
        Assert.DoesNotContain("do-not-leak", result.Payload.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rate_limited_response_maps_to_a_distinct_code()
    {
        var handler = new RecordingHandler
        {
            Respond = _ => (System.Net.HttpStatusCode.PreconditionFailed, "<html>blocked</html>"),
        };
        using var plugin = Create(handler: handler, grants: Grant);
        await plugin.StartAsync(default);
        var tool = plugin.Contributions.Tools.Single(item => item.Descriptor.Name == "social.bilibili");

        var result = await tool.InvokeAsync(new(Scope, Arguments(new { query = "fixture" })), default);

        Assert.False(result.Success);
        Assert.Equal("SOCIAL_RATE_LIMITED", result.ErrorCode);
    }

    [Fact]
    public async Task Successful_read_carries_provenance_and_discloses_limited_coverage()
    {
        var payload = """
            {"code":0,"data":{"bvid":"BV1wExv6MEfT","title":"fixture title",
            "desc":"fixture description","pubdate":1793500000,
            "owner":{"name":"fixture-author"},
            "stat":{"view":1234,"like":56,"reply":7}}}
            """;
        var handler = new RecordingHandler { Respond = _ => (System.Net.HttpStatusCode.OK, payload) };
        using var plugin = Create(handler: handler, grants: Grant);
        await plugin.StartAsync(default);
        var tool = plugin.Contributions.Tools.Single(item => item.Descriptor.Name == "social.bilibili");

        var result = await tool.InvokeAsync(new(Scope, Arguments(
            new { reference = "https://www.bilibili.com/video/BV1wExv6MEfT" })), default);

        Assert.True(result.Success, result.ErrorCode);
        var root = result.Payload;
        Assert.Equal("bilibili", root.GetProperty("platform").GetString());
        Assert.Equal(1, root.GetProperty("returnedResults").GetInt32());
        Assert.False(root.GetProperty("exhaustive").GetBoolean());
        Assert.Equal("response_snapshot_sha256", root.GetProperty("sourceVersionKind").GetString());
        Assert.Matches("^[A-F0-9]{64}$", root.GetProperty("sourceVersion").GetString()!);
        Assert.False(string.IsNullOrEmpty(root.GetProperty("capturedAtUtc").GetString()));
        var source = Assert.Single(root.GetProperty("results").EnumerateArray());
        Assert.Equal("fixture title", source.GetProperty("title").GetString());
        Assert.Equal("fixture-author", source.GetProperty("author").GetString());
        // Provenance identifies the response snapshot, not the request the model sent.
        Assert.DoesNotContain("BV1wExv6MEfT", root.GetProperty("sourceVersion").GetString()!);
    }

    private static JsonElement Arguments(object value) => JsonSerializer.SerializeToElement(value);

    private static SocialSourcesPlugin Create(RecordingHandler? handler = null,
        Func<string, SocialSourceSettings?>? grants = null, Func<ToolScope, bool>? scopeCurrent = null)
    {
        handler ??= new RecordingHandler();
        grants ??= _ => null;
        return new SocialSourcesPlugin(
            () => grants(SocialSourceSettings.Bilibili),
            () => grants(SocialSourceSettings.Xiaohongshu),
            () => grants(SocialSourceSettings.Xiaoheihe),
            () => grants(SocialSourceSettings.Bilibili) is null ? "SOCIAL_NOT_CONFIGURED" : null,
            scopeCurrent ?? (_ => true),
            new SocialHttpGateway(handler));
    }

    /// <summary>
    /// Records requests and routes them by path: the anonymous bootstrap endpoints always answer with a
    /// usable fingerprint so a case can target exactly one business endpoint. Never opens a socket.
    /// </summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private const string Fingerprint =
            """{"code":0,"data":{"b_3":"buvid-three","b_4":"buvid-four"},"message":"ok"}""";
        private const string Nav =
            """{"code":-101,"data":{"wbi_img":{"img_url":"https://i0.hdslb.com/bfs/wbi/7cd084941338484aae1ad9425b84077c.png","sub_url":"https://i0.hdslb.com/bfs/wbi/4932caff0ff746eab6f01bf08b70ac45.png"}}}""";

        public int Calls { get; private set; }
        public Func<HttpRequestMessage, (System.Net.HttpStatusCode Status, string Body)>? Respond { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var (status, body) = path switch
            {
                "/x/frontend/finger/spi" => (System.Net.HttpStatusCode.OK, Fingerprint),
                "/x/web-interface/nav" => (System.Net.HttpStatusCode.OK, Nav),
                _ => Respond?.Invoke(request) ?? (System.Net.HttpStatusCode.OK, """{"code":0}"""),
            };
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}
