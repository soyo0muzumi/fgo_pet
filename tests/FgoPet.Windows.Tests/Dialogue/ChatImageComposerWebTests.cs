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
            async Task Key(string key, int virtualKey)
            {
                await web.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", JsonSerializer.Serialize(new { type = "keyDown", key, code = key, windowsVirtualKeyCode = virtualKey, text = key == "Enter" ? "\r" : "", unmodifiedText = key == "Enter" ? "\r" : "" }));
                await web.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", JsonSerializer.Serialize(new { type = "keyUp", key, code = key, windowsVirtualKeyCode = virtualKey }));
            }
            async Task Capture(string name)
            {
                var evidence = Environment.GetEnvironmentVariable("FGO_PET_UI_EVIDENCE_ROOT");
                if (string.IsNullOrWhiteSpace(evidence)) return;
                Directory.CreateDirectory(evidence);
                await web.CoreWebView2.ExecuteScriptAsync("document.getElementById('composer').focus()");
                using var image = File.Create(Path.Combine(evidence, name + ".png"));
                await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, image);
            }
            await MarginWebIntegrationTests.WaitUntil(() => Js("!!document.querySelector('.composer-tools button[aria-label=\"添加图片与上下文\"]:not(:disabled)')"));
            Assert.True(await Js("parseFloat(getComputedStyle(document.querySelector('.model-chip')).fontSize)>=12 && parseFloat(getComputedStyle(document.querySelector('.chat-footer'),'::after').fontSize)>=11"));
            await web.CoreWebView2.ExecuteScriptAsync("window.contextButton=()=>document.querySelector('.composer-tools button[aria-label=\"添加图片与上下文\"]'); window.imageButton=()=>document.querySelector('.sheet-body .context-actions button'); window.imageChip=()=>document.querySelector('.image-chip');");
            Assert.True(await Js("contextButton().closest('.composer-tools')!==null && contextButton().textContent.trim()==='' && !!contextButton().querySelector('svg')"));
            Assert.True(await Js("contextButton().getAttribute('aria-label')==='添加图片与上下文' && contextButton().title==='添加图片与上下文'"));
            Assert.True(await Js("!document.body.innerText.includes('Tencent CodeBuddy') && !document.querySelector('.attachment-strip')"));
            await Capture("01-empty-image-icon");
            // Keyboard setup must not depend on optional screenshot collection.
            await web.CoreWebView2.ExecuteScriptAsync("document.getElementById('composer').focus()");
            await Key("Tab", 9);
            Assert.True(await Js("document.activeElement===contextButton() && getComputedStyle(document.activeElement).outlineWidth==='2px' && document.activeElement.title==='添加图片与上下文'"));
            await Key("Enter", 13);
            try { await MarginWebIntegrationTests.WaitUntil(() => Js("!!imageButton() && document.activeElement.getAttribute('aria-label')==='关闭面板'")); }
            catch
            {
                Console.WriteLine("IMAGE_PANEL_METADATA_ONLY " + await web.CoreWebView2.ExecuteScriptAsync("JSON.stringify({panel:!!document.querySelector('.sheet'),imageAction:!!imageButton(),activeTag:document.activeElement.tagName,activeLabel:document.activeElement.getAttribute('aria-label'),focused:document.hasFocus()})"));
                throw;
            }
            Assert.True(await Js("document.querySelector('.sheet').innerText.includes('图片与上下文') && !document.querySelector('.sheet').innerText.includes('Tencent CodeBuddy') && !document.querySelector('.sheet').innerText.includes('选择窗口内容')"));
            await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('.image-disclosure summary').click()");
            Assert.True(await Js("document.querySelector('.sheet').innerText.includes('Tencent CodeBuddy')"));
            await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('.image-disclosure summary').click()");
            await Key("Tab", 9);
            Assert.True(await Js("document.activeElement===imageButton() && getComputedStyle(document.activeElement).outlineWidth==='2px' && document.activeElement.textContent.includes('添加图片')"));
            await Key("Enter", 13);
            await MarginWebIntegrationTests.WaitUntil(() => Js("!!imageChip() && !document.querySelector('.sheet')"));
            Assert.Equal(1, session.Choices);
            Assert.True(await Js("imageChip().textContent.includes('PNG · 64 × 48') && !imageChip().querySelector('button[aria-label=\"移除图片\"]').disabled && imageChip().title.includes('Sending shares')"));
            Assert.True(await Js("!document.body.innerText.includes('Tencent CodeBuddy') && document.querySelector('.attachment-strip').scrollHeight<=document.querySelector('.attachment-strip').clientHeight"));
            await Capture("02-selected-image");
            await web.CoreWebView2.CallDevToolsProtocolMethodAsync("Emulation.setDeviceMetricsOverride", "{\"width\":328,\"height\":640,\"deviceScaleFactor\":1,\"mobile\":false}");
            Assert.True(await Js("document.documentElement.scrollWidth<=innerWidth && document.querySelector('.attachment-strip').scrollHeight<=document.querySelector('.attachment-strip').clientHeight"));
            await Capture("03-narrow-selected-image");
            await web.CoreWebView2.ExecuteScriptAsync("imageChip().querySelector('button[aria-label=\"移除图片\"]').click()");
            await MarginWebIntegrationTests.WaitUntil(() => Js("!imageChip() && !document.querySelector('.attachment-strip')"));
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
            Assert.True(await Js("!!imageChip()"));
            Assert.DoesNotContain("chat.send", session.Commands);
            await Capture("05-trusted-drop-image");
            session.Publish(streaming: true);
            await web.CoreWebView2.ExecuteScriptAsync("contextButton().click()");
            await MarginWebIntegrationTests.WaitUntil(() => Js("!!imageButton() && imageButton().disabled"));
            await web.CoreWebView2.ExecuteScriptAsync("imageButton().click()");
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
