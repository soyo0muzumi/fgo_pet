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

public sealed class ChatImageComposerWebTests
{
    [Fact]
    public Task Image_icon_preserves_keyboard_host_actions_without_permanent_disclosure_or_empty_context_row() => StaRunner.RunAsync(async () =>
    {
        var session = new FixtureSession();
        var storage = Path.Combine(Path.GetTempPath(), "fgopet-image-composer-" + Guid.NewGuid().ToString("N"));
        using var host = new ChatWebSurfaceFactory(new FixtureFactory(session), storage).CreateView(new FixtureActions());
        var window = new Window { Content = host, Width = 500, Height = 700, ShowInTaskbar = false, Left = -10000, Top = -10000 };
        try
        {
            window.Show();
            await MarginWebIntegrationTests.WaitUntil(() => Task.FromResult(host.State == WebSurfaceState.Ready));
            var web = Assert.IsType<WebView2>(host.Children[0]);
            // This off-screen fixture must emulate document focus before sending real keyboard events.
            await web.CoreWebView2.CallDevToolsProtocolMethodAsync("Emulation.setFocusEmulationEnabled", "{\"enabled\":true}");
            async Task<bool> Js(string code) => await web.CoreWebView2.ExecuteScriptAsync(code) == "true";
            async Task Capture(string name)
            {
                var evidence = Environment.GetEnvironmentVariable("FGO_PET_UI_EVIDENCE_ROOT");
                if (string.IsNullOrWhiteSpace(evidence)) return;
                Directory.CreateDirectory(evidence);
                await web.CoreWebView2.ExecuteScriptAsync("document.getElementById('composer').focus()");
                using var image = File.Create(Path.Combine(evidence, name + ".png"));
                await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, image);
            }
            await MarginWebIntegrationTests.WaitUntil(() => Js("!document.getElementById('choose-image').hidden"));
            Assert.True(await Js("document.getElementById('choose-image').closest('.composer-toolbar')!==null && document.getElementById('choose-image').textContent.trim()==='' && !!document.querySelector('#choose-image use[href=\"#icon-image\"]')"));
            Assert.True(await Js("document.getElementById('choose-image').getAttribute('aria-label')==='Choose image' && document.getElementById('choose-image').title==='Choose image'"));
            Assert.True(await Js("!document.getElementById('image-disclosure') && !document.body.innerText.includes('Tencent CodeBuddy') && document.getElementById('context-bar').hidden && document.getElementById('context-bar').getBoundingClientRect().height===0"));
            await Capture("01-empty-image-icon");
            // Keyboard setup must not depend on optional screenshot collection.
            await web.CoreWebView2.ExecuteScriptAsync("document.getElementById('composer').focus()");
            await web.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", "{\"type\":\"keyDown\",\"key\":\"Tab\",\"code\":\"Tab\",\"windowsVirtualKeyCode\":9}");
            await web.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", "{\"type\":\"keyUp\",\"key\":\"Tab\",\"code\":\"Tab\",\"windowsVirtualKeyCode\":9}");
            Assert.True(await Js("document.activeElement.id==='choose-image' && getComputedStyle(document.activeElement).outlineWidth==='2px' && !document.getElementById('chat-tooltip').hidden && document.getElementById('chat-tooltip').textContent==='Choose image'"),
                await web.CoreWebView2.ExecuteScriptAsync("JSON.stringify({active:document.activeElement.id,focused:document.hasFocus(),focusVisible:document.activeElement.matches(':focus-visible'),outline:getComputedStyle(document.activeElement).outlineWidth,tooltipHidden:document.getElementById('chat-tooltip').hidden,tooltipText:document.getElementById('chat-tooltip').textContent})"));
            await web.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", "{\"type\":\"keyDown\",\"key\":\"Enter\",\"code\":\"Enter\",\"windowsVirtualKeyCode\":13,\"text\":\"\\r\",\"unmodifiedText\":\"\\r\"}");
            await web.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", "{\"type\":\"keyUp\",\"key\":\"Enter\",\"code\":\"Enter\",\"windowsVirtualKeyCode\":13}");
            await MarginWebIntegrationTests.WaitUntil(() => Js("!document.getElementById('image-selection').hidden"));
            Assert.Equal(1, session.Choices);
            Assert.True(await Js("document.getElementById('image-label').textContent==='PNG · 64 × 48' && !document.getElementById('clear-image').disabled && document.getElementById('image-selection').title.includes('Sending shares')"));
            Assert.True(await Js("!document.body.innerText.includes('Tencent CodeBuddy') && document.getElementById('context-bar').scrollHeight<=document.getElementById('context-bar').clientHeight"));
            await Capture("02-selected-image");
            await web.CoreWebView2.CallDevToolsProtocolMethodAsync("Emulation.setDeviceMetricsOverride", "{\"width\":328,\"height\":640,\"deviceScaleFactor\":1,\"mobile\":false}");
            Assert.True(await Js("document.documentElement.scrollWidth<=innerWidth && document.getElementById('context-bar').scrollHeight<=document.getElementById('context-bar').clientHeight"));
            await Capture("03-narrow-selected-image");
            await web.CoreWebView2.ExecuteScriptAsync("document.getElementById('clear-image').click()");
            await MarginWebIntegrationTests.WaitUntil(() => Js("document.getElementById('image-selection').hidden && document.getElementById('context-bar').hidden"));
            Assert.Equal(1, session.Clears);
            await Capture("04-narrow-empty-image-icon");
            // CDP delivers a trusted browser drop with a synthetic local PNG; no user clipboard is touched.
            var droppedImage = Path.Combine(storage, "synthetic-drop.png");
            Directory.CreateDirectory(storage);
            await File.WriteAllBytesAsync(droppedImage, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));
            using (var rect = JsonDocument.Parse(await web.CoreWebView2.ExecuteScriptAsync("(()=>{const r=document.getElementById('composer').getBoundingClientRect();return {x:r.x+r.width/2,y:r.y+r.height/2}})()")))
            {
                foreach (var type in new[] { "dragEnter", "dragOver", "drop" })
                    await web.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchDragEvent", JsonSerializer.Serialize(new
                    {
                        type, x = rect.RootElement.GetProperty("x").GetDouble(), y = rect.RootElement.GetProperty("y").GetDouble(),
                        data = new { items = Array.Empty<object>(), files = new[] { droppedImage }, dragOperationsMask = 1 }
                    }));
            }
            await MarginWebIntegrationTests.WaitUntil(() => Task.FromResult(session.Imports == 1));
            Assert.True(await Js("!document.getElementById('image-selection').hidden"));
            Assert.DoesNotContain("chat.send", session.Commands);
            await Capture("05-trusted-drop-image");
            session.Publish(streaming: true);
            await MarginWebIntegrationTests.WaitUntil(() => Js("document.getElementById('choose-image').disabled"));
            await web.CoreWebView2.ExecuteScriptAsync("document.getElementById('choose-image').click()");
            Assert.Equal(1, session.Choices);
            Assert.DoesNotContain("chat.send", session.Commands);
            Console.WriteLine("IMAGE_COMPOSER_METADATA_ONLY empty=true selected=true narrow=true keyboard=true hostChoose=1 hostClear=1 streamingDisabled=true modelCalls=0");
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
        private long version = 1;
        private bool selected, streaming;
        public int Choices, Clears, Imports;
        private readonly ChatImageTransfer transfer = new();
        public List<string> Commands { get; } = [];
        public event Action<ChatWebSnapshot>? Changed;
        public void Publish(bool streaming) { this.streaming=streaming; version++; Changed?.Invoke(ReadSnapshot()); }
        public ChatWebSnapshot ReadSnapshot() => new(new("synthetic-session", "synthetic-role", "synthetic-conversation"), version,
            new("", 1), new("Synthetic companion", null, [], [], Actions: new(CanChooseImage: !streaming, CanClearImage: selected && !streaming),
                ImageLabel: selected ? "PNG · 64 × 48" : null,
                ImageDisclosure: "Sending shares this synthetic image with Tencent CodeBuddy CN through local Buddy. Selection alone does not send."),
            new([], streaming, false, "", "", "", false, "", "", false, true, streaming, false, false, new("", ""), new("", ""), [], []),
            new([], "", false, false, true, null, null, false));
        public ValueTask<WebSurfaceCommandResult> HandleCommandAsync(WebSurfaceMessage message, CancellationToken cancellationToken)
        {
            Commands.Add(message.Type);
            if (message.Type == "chat.image")
            {
                Assert.Equal("synthetic-session", message.Payload.GetProperty("sessionId").GetString());
                var p = message.Payload;
                var bytes = transfer.Append(p.GetProperty("transferId").GetString()!, p.GetProperty("mimeType").GetString()!,
                    p.GetProperty("offset").GetInt64(), p.GetProperty("data").GetString()!, p.GetProperty("complete").GetBoolean());
                if (bytes is not null) { Assert.True(bytes.Length > 8); Imports++; selected=true; version++; }
            }
            if (message.Type == "chat.host")
            {
                Assert.Equal("synthetic-session", message.Payload.GetProperty("sessionId").GetString());
                var action = message.Payload.GetProperty("action").GetString();
                if (action == "chooseImage") { Choices++; selected=true; }
                if (action == "clearImage") { Clears++; selected=false; }
                version++;
            }
            return ValueTask.FromResult(new WebSurfaceCommandResult(true, new { snapshot = ReadSnapshot() }));
        }
        public void Dispose() => transfer.Dispose();
    }
}
