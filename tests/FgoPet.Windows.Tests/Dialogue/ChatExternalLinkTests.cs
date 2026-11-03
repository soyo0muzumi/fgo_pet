using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FgoPet.App.Dialogue;
using FgoPet.Dialogue.Contracts;
using FgoPet.UiSdk;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Web.WebView2.Core;
using Xunit;

namespace FgoPet.Windows.Tests.Dialogue;

public sealed class ChatExternalLinkTests
{
    [Fact]
    public Task Markdown_link_click_is_forwarded_once_and_chat_stays_on_its_local_page() => StaRunner.RunAsync(async () =>
    {
        var opened = new List<Uri>();
        var storage = Path.Combine(Path.GetTempPath(), "fgopet-chat-links-" + Guid.NewGuid().ToString("N"));
        var factory = new ChatWebSurfaceFactory(new FixtureFactory(), storage, openExternalLink: opened.Add);
        using var host = factory.CreateView(new FixtureActions());
        var window = new Window { Content = host, Width = 500, Height = 800, ShowInTaskbar = false, Left = -10000, Top = -10000 };
        try
        {
            window.Show();
            await MarginWebIntegrationTests.WaitUntil(() => Task.FromResult(host.State == WebSurfaceState.Ready));
            var web = Assert.IsType<WebView2>(host.Children[0]);
            var core = web.CoreWebView2;
            await core.CallDevToolsProtocolMethodAsync("Emulation.setFocusEmulationEnabled", "{\"enabled\":true}");
            await MarginWebIntegrationTests.WaitUntil(async () => await core.ExecuteScriptAsync("!!document.querySelector('.message a[href=\"https://example.org/article#detail\"]')") == "true");
            Assert.Empty(opened);
            await core.ExecuteScriptAsync("window.open('https://example.org/automatic','_blank')");
            Assert.Empty(opened);
            await Click(core, ".message a");
            await MarginWebIntegrationTests.WaitUntil(() => Task.FromResult(opened.Count == 1));
            Assert.Equal("https://example.org/article#detail", opened[0].AbsoluteUri);
            Assert.Equal("https://chat.fgopet.invalid/index.html", core.Source);
            Assert.Equal(WebSurfaceState.Ready, host.State);
        }
        finally { window.Close(); }
    }, TimeSpan.FromSeconds(35));

    [Fact]
    public Task Markdown_image_previews_inline_automatically_and_can_expand_and_open_its_source() => StaRunner.RunAsync(async () =>
    {
        var loaded = new List<Uri>(); var opened = new List<Uri>();
        var preview = SyntheticPreview();
        var storage = Path.Combine(Path.GetTempPath(), "fgopet-chat-images-" + Guid.NewGuid().ToString("N"));
        var factory = new ChatWebSurfaceFactory(new FixtureFactory("一起看看这张图：\n\n![合成配图](https://example.org/picture.png)"),
            storage, openExternalLink: opened.Add, loadImagePreview: async (uri, token) =>
            {
                loaded.Add(uri);
                await Task.Delay(30, token);
                return preview;
            });
        using var host = factory.CreateView(new FixtureActions());
        var window = new Window { Content = host, Width = 360, Height = 700, ShowInTaskbar = false, Left = -10000, Top = -10000 };
        try
        {
            window.Show();
            await MarginWebIntegrationTests.WaitUntil(() => Task.FromResult(host.State == WebSurfaceState.Ready));
            var core = Assert.IsType<WebView2>(host.Children[0]).CoreWebView2;
            await core.CallDevToolsProtocolMethodAsync("Emulation.setFocusEmulationEnabled", "{\"enabled\":true}");
            await MarginWebIntegrationTests.WaitUntil(async () => await core.ExecuteScriptAsync("!!document.querySelector('.reply-image-thumbnail img')?.naturalWidth") == "true");
            Assert.Equal("https://example.org/picture.png", Assert.Single(loaded).AbsoluteUri);
            Assert.Empty(opened);
            Assert.Equal("true", await core.ExecuteScriptAsync("[...document.images].every(i=>!i.src.startsWith('https://example.org'))"));
            Assert.Equal("true", await core.ExecuteScriptAsync("document.documentElement.scrollWidth <= innerWidth"));
            Assert.Equal("true", await core.ExecuteScriptAsync("(()=>{const i=document.querySelector('.reply-image-thumbnail img'),r=i.getBoundingClientRect(),m=i.closest('.message').getBoundingClientRect();return i.naturalWidth===800 && r.right<=m.right+1 && r.width<=m.width && r.height<=260})()"));
            await Capture(core, "chat-image-inline-synthetic.png");
            await Click(core, ".reply-image-thumbnail");
            await MarginWebIntegrationTests.WaitUntil(async () => await core.ExecuteScriptAsync("!!document.querySelector('.reply-image-dialog[open] img')") == "true");
            await Capture(core, "chat-image-expanded-synthetic.png");
            await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", "{\"type\":\"keyDown\",\"key\":\"Escape\",\"code\":\"Escape\",\"windowsVirtualKeyCode\":27}");
            await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", "{\"type\":\"keyUp\",\"key\":\"Escape\",\"code\":\"Escape\",\"windowsVirtualKeyCode\":27}");
            await MarginWebIntegrationTests.WaitUntil(async () => await core.ExecuteScriptAsync("!document.querySelector('.reply-image-dialog[open]')") == "true");
            await Click(core, ".reply-image-source");
            await MarginWebIntegrationTests.WaitUntil(() => Task.FromResult(opened.Count == 1));
            Assert.Equal("https://example.org/picture.png", opened[0].AbsoluteUri);
            Assert.Equal("https://chat.fgopet.invalid/index.html", core.Source);
        }
        finally { window.Close(); }
    }, TimeSpan.FromSeconds(35));

    private static string SyntheticPreview()
    {
        var pixels = new byte[800 * 500 * 4];
        for (var y = 0; y < 500; y++) for (var x = 0; x < 800; x++)
        {
            var offset = (y * 800 + x) * 4;
            pixels[offset] = (byte)(145 + y / 5); pixels[offset + 1] = (byte)(180 + x / 12);
            pixels[offset + 2] = 235; pixels[offset + 3] = 255;
        }
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(800, 500, 96, 96, PixelFormats.Bgra32, null, pixels, 800 * 4)));
        using var output = new MemoryStream(); encoder.Save(output);
        return "data:image/png;base64," + Convert.ToBase64String(output.ToArray());
    }

    private static async Task Capture(CoreWebView2 core, string name)
    {
        var evidence = Environment.GetEnvironmentVariable("FGO_PET_UI_EVIDENCE_ROOT");
        if (string.IsNullOrWhiteSpace(evidence)) return;
        Directory.CreateDirectory(evidence);
        using var output = File.Create(Path.Combine(evidence, name));
        await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, output);
    }

    [Fact]
    public Task Image_failure_keeps_original_source_and_retry_in_the_message() => StaRunner.RunAsync(async () =>
    {
        var attempts = 0;
        var factory = new ChatWebSurfaceFactory(new FixtureFactory("![合成配图](https://example.org/unavailable.png)"),
            Path.Combine(Path.GetTempPath(), "fgopet-chat-image-failure-" + Guid.NewGuid().ToString("N")),
            openExternalLink: _ => { }, loadImagePreview: (_, _) => { attempts++; return Task.FromResult<string?>(null); });
        using var host = factory.CreateView(new FixtureActions());
        var window = new Window { Content = host, Width = 360, Height = 700, ShowInTaskbar = false, Left = -10000, Top = -10000 };
        try
        {
            window.Show();
            await MarginWebIntegrationTests.WaitUntil(() => Task.FromResult(host.State == WebSurfaceState.Ready));
            var core = Assert.IsType<WebView2>(host.Children[0]).CoreWebView2;
            await core.CallDevToolsProtocolMethodAsync("Emulation.setFocusEmulationEnabled", "{\"enabled\":true}");
            await MarginWebIntegrationTests.WaitUntil(async () => await core.ExecuteScriptAsync("document.querySelector('.reply-image-load')?.textContent === '重试加载配图'") == "true");
            Assert.Equal(1, attempts);
            Assert.Equal("true", await core.ExecuteScriptAsync("!!document.querySelector('.reply-image-source[href=\"https://example.org/unavailable.png\"]')"));
            await Click(core, ".reply-image-load");
            await MarginWebIntegrationTests.WaitUntil(() => Task.FromResult(attempts == 2));
            Assert.Equal(WebSurfaceState.Ready, host.State);
        }
        finally { window.Close(); }
    }, TimeSpan.FromSeconds(35));

    internal static Task WaitForAnimations(CoreWebView2 core) => MarginWebIntegrationTests.WaitUntil(async () =>
        await core.ExecuteScriptAsync("document.getAnimations().every(a=>a.effect?.getComputedTiming().iterations===Infinity||a.playState==='finished'||a.playState==='idle')") == "true");

    internal static async Task Click(CoreWebView2 core, string selector)
    {
        var expression = "document.querySelector(" + JsonSerializer.Serialize(selector) + ")";
        await core.ExecuteScriptAsync(expression + ".scrollIntoView({block:'center',behavior:'instant'})");
        string? lastPoint = null;
        var stableSamples = 0;
        await MarginWebIntegrationTests.WaitUntil(async () =>
        {
            // Layout/entry animations and compositor hit targets settle after the DOM is ready.
            var current = await core.ExecuteScriptAsync("(()=>{const e=" + expression + ";if(!e)return null;const r=e.getBoundingClientRect(),x=r.x+r.width/2,y=r.y+r.height/2;return r.width>0&&r.height>0&&e.contains(document.elementFromPoint(x,y))?{x,y}:null;})()");
            stableSamples = current != "null" && current == lastPoint ? stableSamples + 1 : 0;
            lastPoint = current;
            return stableSamples >= 2;
        });
        using var point = JsonDocument.Parse(lastPoint!);
        var x = point.RootElement.GetProperty("x").GetDouble(); var y = point.RootElement.GetProperty("y").GetDouble();
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type = "mouseMoved", x, y }));
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type = "mousePressed", x, y, button = "left", clickCount = 1 }));
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type = "mouseReleased", x, y, button = "left", clickCount = 1 }));
    }

    private sealed class FixtureFactory(string text = "这篇文章值得一起看看：[打开原文](https://example.org/article#detail)") : IChatWebSessionFactory
    { public IChatWebSession Create(IChatWebHostActions host) => new FixtureSession(text); }
    private sealed class FixtureActions : IChatWebHostActions
    {
        public event EventHandler? Changed { add { } remove { } }
        public ChatWebHostPresentation ReadPresentation() => new("Synthetic companion", null, [], [], Window: new("conversation", false));
        public ValueTask<WebSurfaceCommandResult> HandleAsync(ChatWebHostRequest request, CancellationToken token) => ValueTask.FromResult(new WebSurfaceCommandResult(false));
    }
    private sealed class FixtureSession(string text) : IChatWebSession
    {
        public event Action<ChatWebSnapshot>? Changed { add { } remove { } }
        public ChatWebSnapshot ReadSnapshot() => new(new("synthetic-session", "synthetic-role", "synthetic-conversation"), 1,
            new("", 1), new("Synthetic companion", null, [], [], Window: new("conversation", false)),
            new([new("synthetic-reply", "assistant", text, false, "", "", "", false, false, false, false, false, false, "", "", false)],
                false, false, "", "", "", false, "", "", false, true, false, false, false, new("", ""), new("", ""), [], []),
            new([], "", false, false, true, null, null, false));
        public ValueTask<WebSurfaceCommandResult> HandleCommandAsync(WebSurfaceMessage message, CancellationToken token) =>
            ValueTask.FromResult(new WebSurfaceCommandResult(true, new { snapshot = ReadSnapshot() }));
        public void Dispose() { }
    }
}
