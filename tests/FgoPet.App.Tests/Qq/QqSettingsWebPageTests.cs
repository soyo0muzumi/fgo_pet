using System.Text.Json;
using FgoPet.Plugin.Qq;
using FgoPet.Plugin.Qq.Desktop;
using FgoPet.UiSdk;
using Xunit;

namespace FgoPet.App.Tests.Qq;

public sealed class QqSettingsWebPageTests
{
    [Theory]
    [InlineData("qq.get", "[]")]
    [InlineData("qq.get", "{\"pageId\":7}")]
    [InlineData("qq.get", "{\"pageId\":\"Other\"}")]
    [InlineData("qq.get", "{\"pageId\":\"Qq\",\"pageId\":\"Qq\"}")]
    [InlineData("qq.get", "{\"pageId\":\"Qq\",\"appSecret\":\"fixture\"}")]
    [InlineData("qq.configure", "{\"pageId\":\"Qq\",\"appId\":\"123\",\"appId\":\"456\"}")]
    [InlineData("qq.configure", "{\"pageId\":\"Qq\",\"appId\":123}")]
    [InlineData("qq.configure", "{\"pageId\":\"Qq\",\"appId\":\"１２３\"}")]
    [InlineData("qq.configure", "{\"pageId\":\"Qq\",\"appId\":\"\"}")]
    [InlineData("qq.configure", "{\"pageId\":\"Qq\",\"appId\":\"123456789012345678901234567890123\"}")]
    [InlineData("qq.configure", "{\"pageId\":\"Qq\",\"appId\":\"123\",\"appSecret\":true}")]
    [InlineData("qq.configure", "{\"pageId\":\"Qq\",\"appId\":\"123\",\"appSecret\":\"line\\nfeed\"}")]
    [InlineData("qq.configure", "{\"pageId\":\"Qq\",\"appId\":\"123\",\"endpoint\":\"https://fixture.invalid\"}")]
    [InlineData("qq.beginPairing", "{\"pageId\":\"Qq\",\"openId\":\"fixture-user\"}")]
    [InlineData("qq.setEnabled", "{\"pageId\":\"Qq\",\"enabled\":\"true\"}")]
    [InlineData("qq.setEnabled", "{\"pageId\":\"Qq\"}")]
    [InlineData("qq.bindCurrentConversation", "{\"pageId\":\"Qq\",\"conversationId\":\"other\"}")]
    [InlineData("qq.reset", "{\"pageId\":\"Qq\",\"appId\":\"123\"}")]
    public async Task Invalid_or_forged_payload_does_not_call_channel(string command, string payload)
    {
        var owner = new FixtureOwner();
        var result = await Send(owner.Page(), command, payload);

        Assert.False(result.Success);
        Assert.Equal("SETTINGS_INVALID_INPUT", result.ErrorCode);
        Assert.Null(result.Payload);
        Assert.Empty(owner.Calls);
    }

    [Fact]
    public async Task Oversized_secret_and_unknown_command_do_not_call_channel()
    {
        var owner = new FixtureOwner();
        var payload = JsonSerializer.Serialize(new { pageId = "Qq", appId = "123", appSecret = new string('x', 4097) });

        Assert.Equal("SETTINGS_INVALID_INPUT", (await Send(owner.Page(), "qq.configure", payload)).ErrorCode);
        Assert.Equal("SETTINGS_UNKNOWN_COMMAND", (await Send(owner.Page(), "qq.send", "{\"pageId\":\"Qq\"}")).ErrorCode);
        Assert.Empty(owner.Calls);
    }

    [Fact]
    public async Task Configure_passes_secret_only_to_owner_and_never_returns_it()
    {
        const string secret = "synthetic-write-only-secret";
        var owner = new FixtureOwner();
        var page = owner.Page();
        var configured = await Send(page, "qq.configure", JsonSerializer.Serialize(new
        {
            pageId = "Qq", appId = " 123 ", appSecret = secret,
        }));
        var reread = await Send(page, "qq.get", "{\"pageId\":\"Qq\"}");

        Assert.True(configured.Success);
        Assert.Equal("123", owner.AppId);
        Assert.Equal(secret, owner.Secret);
        Assert.True(Assert.IsType<QqChannelSnapshot>(configured.Payload).HasSecret);
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(configured));
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(reread));
        Assert.DoesNotContain("AppSecret", JsonSerializer.Serialize(reread));
        Assert.Equal(new[] { "configure", "get" }, owner.Calls);
    }

    [Theory]
    [InlineData("{\"pageId\":\"Qq\",\"appId\":\"123\"}", null)]
    [InlineData("{\"pageId\":\"Qq\",\"appId\":\"123\",\"appSecret\":null}", null)]
    [InlineData("{\"pageId\":\"Qq\",\"appId\":\"123\",\"appSecret\":\"\"}", "")]
    public async Task Blank_secret_is_forwarded_without_reading_or_echoing_existing_key(string payload, string? expected)
    {
        var owner = new FixtureOwner();
        Assert.True((await Send(owner.Page(), "qq.configure", payload)).Success);
        Assert.Equal(expected, owner.Secret);
        Assert.Equal(new[] { "configure" }, owner.Calls);
    }

    [Theory]
    [InlineData("qq.get", "{\"pageId\":\"Qq\"}")]
    [InlineData("qq.configure", "{\"pageId\":\"Qq\",\"appId\":\"123\"}")]
    [InlineData("qq.beginPairing", "{\"pageId\":\"Qq\"}")]
    [InlineData("qq.setEnabled", "{\"pageId\":\"Qq\",\"enabled\":true}")]
    [InlineData("qq.bindCurrentConversation", "{\"pageId\":\"Qq\"}")]
    [InlineData("qq.reset", "{\"pageId\":\"Qq\"}")]
    public async Task Cancelled_route_rejects_work_before_owner_entry(string command, string payload)
    {
        var owner = new FixtureOwner();
        using var route = new CancellationTokenSource();
        route.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Send(owner.Page(), command, payload, route.Token));
        Assert.Empty(owner.Calls);
    }

    [Fact]
    public async Task Pending_write_propagates_route_cancellation_to_owner()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = new FixtureOwner();
        var committed = false;
        using var route = new CancellationTokenSource();
        var page = owner.Page(configure: async (_, _, token) =>
        {
            Assert.Equal(route.Token, token);
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            token.ThrowIfCancellationRequested();
            committed = true;
            return Snapshot;
        });
        var write = Send(page, "qq.configure", "{\"pageId\":\"Qq\",\"appId\":\"123\"}", route.Token);
        await entered.Task;
        route.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
        Assert.False(committed);
    }

    [Fact]
    public async Task Late_snapshot_from_owner_is_discarded_after_route_cancellation()
    {
        var completion = new TaskCompletionSource<QqChannelSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = new FixtureOwner();
        using var route = new CancellationTokenSource();
        var page = owner.Page(configure: (_, _, _) => completion.Task);
        var write = Send(page, "qq.configure", "{\"pageId\":\"Qq\",\"appId\":\"123\"}", route.Token);
        route.Cancel();
        completion.SetResult(Snapshot);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
    }

    [Fact]
    public async Task Provider_exception_text_never_reaches_settings_result()
    {
        const string sensitive = "synthetic-secret-and-response";
        var owner = new FixtureOwner();
        var page = owner.Page(configure: (_, _, _) => throw new InvalidOperationException(sensitive));
        var result = await Send(page, "qq.configure", "{\"pageId\":\"Qq\",\"appId\":\"123\"}");

        Assert.False(result.Success);
        Assert.Null(result.Payload);
        Assert.Equal("QQ_SETTINGS_UNAVAILABLE", result.ErrorCode);
        Assert.DoesNotContain(sensitive, JsonSerializer.Serialize(result));
    }

    private static readonly QqChannelSnapshot Snapshot = new("123", true, false, false,
        "disabled", null, null, null, null);

    private static async Task<WebSurfaceCommandResult> Send(QqSettingsWebPage page, string command,
        string payload, CancellationToken cancellationToken = default)
    {
        using var document = JsonDocument.Parse(payload);
        return await page.HandleCommandAsync(new WebSurfaceMessage(command, "fixture", document.RootElement), cancellationToken);
    }

    private sealed class FixtureOwner
    {
        public List<string> Calls { get; } = [];
        public string? AppId { get; private set; }
        public string? Secret { get; private set; }

        public QqSettingsWebPage Page(Func<string, string?, CancellationToken, Task<QqChannelSnapshot>>? configure = null) => new(
            token => Invoke("get", token),
            configure ?? ((appId, secret, token) =>
            {
                token.ThrowIfCancellationRequested();
                AppId = appId;
                Secret = secret;
                return Invoke("configure", token);
            }),
            token => Invoke("pair", token),
            (_, token) => Invoke("enable", token),
            token => Invoke("bind", token),
            token => Invoke("reset", token));

        private Task<QqChannelSnapshot> Invoke(string command, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Calls.Add(command);
            return Task.FromResult(Snapshot);
        }
    }
}
