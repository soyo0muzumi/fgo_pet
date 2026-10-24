using System.IO;
using System.Text.Json;
using System.Windows;
using FgoPet.App.Dialogue;
using FgoPet.Dialogue.Contracts;
using FgoPet.UiSdk;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Xunit;

namespace FgoPet.Windows.Tests.Dialogue;

public sealed class ChatApprovalWebTests
{
    [Fact]
    public Task Frozen_summary_details_fallback_and_review_only_bridge_render_in_isolated_webview() => StaRunner.RunAsync(async () =>
    {
        var session = new FixtureSession();
        var storage = Path.Combine(Path.GetTempPath(), "fgopet-approval-web-" + Guid.NewGuid().ToString("N"));
        using var host = new ChatWebSurfaceFactory(new FixtureFactory(session), storage).CreateView(new FixtureActions());
        var window = new Window { Content = host, Width = 500, Height = 820, ShowInTaskbar = false, Left = -10000, Top = -10000 };
        try
        {
            window.Show();
            await MarginWebIntegrationTests.WaitUntil(() => Task.FromResult(host.State == WebSurfaceState.Ready));
            var web = Assert.IsType<WebView2>(host.Children[0]);
            async Task<bool> Js(string code) => await web.CoreWebView2.ExecuteScriptAsync(code) == "true";
            await MarginWebIntegrationTests.WaitUntil(() => Js("!!document.querySelector('.approval-card .primary-button')"));
            Assert.True(await Js("!document.body.innerText.includes('CHAT_BRIDGE_UNAVAILABLE') && !document.body.innerText.includes('无法连接桌面宿主')"),
                "A valid initial approval snapshot must not trigger presentation before the native handshake completes.");
            Assert.True(await Js("document.querySelector('.approval-card > p').textContent.includes('Synthetic reminder') && !document.querySelector('.approval-card details').open"));
            Assert.True(await Js("!document.querySelector('.approval-card img') && document.querySelector('.approval-card pre').textContent.endsWith('VERSION 17')"));
            Assert.True(await Js("document.querySelector('.approval-card .text-button').textContent==='取消请求' && document.querySelector('.approval-card .secondary-button').textContent==='这次不允许'"));
            await web.CoreWebView2.CallDevToolsProtocolMethodAsync("Emulation.setDeviceMetricsOverride", "{\"width\":328,\"height\":700,\"deviceScaleFactor\":1,\"mobile\":false}");
            Assert.True(await Js("document.documentElement.scrollWidth<=innerWidth"));
            await MarginWebIntegrationTests.WaitUntil(() => Js("document.querySelector('#messages').getAnimations({subtree:true}).every(a=>a.playState==='finished')"));
            var evidence = Environment.GetEnvironmentVariable("FGO_PET_UI_EVIDENCE_ROOT");
            if (!string.IsNullOrWhiteSpace(evidence))
            {
                Directory.CreateDirectory(evidence);
                using var image = File.Create(Path.Combine(evidence, "chat-approval-synthetic.png"));
                await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, image);
            }
            await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('.approval-card .primary-button').click();document.querySelector('.approval-card .primary-button').click()");
            await MarginWebIntegrationTests.WaitUntil(() => Js("!document.querySelector('.approval-card')"));
            Assert.Equal(1, session.Reviews);
            Assert.DoesNotContain("chat.approval.allow", session.Commands);
            Assert.True(await Js("document.getElementById('composer').value==='Synthetic unsent draft'"));
            session.PublishApproval(summary: false);
            await MarginWebIntegrationTests.WaitUntil(() => Js("!!document.querySelector('.approval-card')"));
            Assert.True(await Js("document.querySelector('.approval-card details').open && !document.querySelector('.approval-card > p').textContent.includes('Synthetic reminder')"));
            Assert.True(await Js("document.querySelector('.approval-card pre').textContent.endsWith('VERSION 17')"));
            session.PublishApproval(summary: true, expired: true);
            await MarginWebIntegrationTests.WaitUntil(() => Js("document.querySelector('.approval-card .primary-button').disabled"));
            Assert.True(await Js("document.querySelector('.approval-card .secondary-button').disabled && !document.querySelector('.approval-card .text-button').disabled"));
            Console.WriteLine("APPROVAL_WEB_METADATA_ONLY summary=true fullFallback=true inert=true narrow=true repeatedReviewOnce=true expired=true modelCalls=0");
        }
        finally { window.Close(); }
    }, TimeSpan.FromSeconds(45));

    private sealed class FixtureFactory(FixtureSession session) : IChatWebSessionFactory
    { public IChatWebSession Create(IChatWebHostActions host) => session; }
    private sealed class FixtureActions : IChatWebHostActions
    {
        public event EventHandler? Changed { add { } remove { } }
        public ChatWebHostPresentation ReadPresentation() => new("Synthetic companion", null, [], []);
        public ValueTask<WebSurfaceCommandResult> HandleAsync(ChatWebHostRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(new WebSurfaceCommandResult(false));
    }
    private sealed class FixtureSession : IChatWebSession
    {
        private long _version = 1;
        private ChatWebApproval? _approval;
        public int Reviews;
        public List<string> Commands { get; } = [];
        public event Action<ChatWebSnapshot>? Changed;
        public FixtureSession() => PublishApproval(true);
        public void PublishApproval(bool summary, bool expired = false)
        {
            _approval = new("synthetic-run", Guid.NewGuid().ToString("N"), 17,
                DateTimeOffset.UtcNow.AddMinutes(expired ? -1 : 5), "reminder.cancel",
                "<img src=x onerror=alert(1)>\nFrozen synthetic arguments\n" + new string('x', 2000) + "\nVERSION 17", false, "")
            { ActionTitle = "Cancel reminder", PlainTextSummary = summary ? "Synthetic reminder: take a short break\nTomorrow 09:00 UTC\nReminder: synthetic-id · version 17\nThis reminder will no longer notify you." : null };
            _version++; Changed?.Invoke(ReadSnapshot());
        }
        public ChatWebSnapshot ReadSnapshot() => new(new("synthetic-session", "synthetic-role", "synthetic-conversation"), _version,
            new("Synthetic unsent draft", 1), new("Synthetic companion", null, [], []),
            new([new("synthetic-user", "user", "Cancel my synthetic break reminder.", false, "", "", "", false, false, false, false, false, false, "", "", false)], false, false, "", "", "", false, "", "", false, _approval is null, _approval is not null, false, false,
                new("", ""), new("", ""), [], []) { PendingApproval = _approval, HasPendingRun = _approval is not null },
            new([], "", false, false, true, null, null, false));
        public async ValueTask<WebSurfaceCommandResult> HandleCommandAsync(WebSurfaceMessage message, CancellationToken cancellationToken)
        {
            Commands.Add(message.Type);
            if (message.Type == "chat.approval.review")
            {
                Reviews++;
                Assert.Equal(new[] { "conversationId", "requestId", "revision", "runId", "servantId", "sessionId" },
                    message.Payload.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
                await Task.Delay(100, cancellationToken); _approval = null; _version++;
            }
            return new(true, new { snapshot = ReadSnapshot() });
        }
        public void Dispose() { }
    }
}
