using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using FgoPet.App.Dialogue;
using FgoPet.App.Settings;
using FgoPet.App.Providers;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Packs;
using FgoPet.Dialogue.Settings;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Infrastructure.Packs;
using FgoPet.Infrastructure.Providers;
using FgoPet.Infrastructure.Secrets;
using FgoPet.Dialogue.Contracts;
using FgoPet.UiSdk;
using Xunit;

namespace FgoPet.Windows.Tests.Dialogue;

/// <summary>Actual local WebView2 assets and bridge, with an isolated offline conversation.</summary>
public sealed class MarginWebIntegrationTests
{
    private sealed class WorkspaceSelectionStub : IChatWorkspaceSelection
    {
        public bool CanChoose => true;
        public string? Label { get; private set; } = "Synthetic workspace";
        public event EventHandler? Changed;
        public int Choices { get; private set; }
        public bool Choose(Func<string?> nativePicker) { Choices++; return true; }
        public void Clear() { Label = null; Changed?.Invoke(this, EventArgs.Empty); }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    [Fact]
    public async Task Paper_menu_keeps_owner_commands_keyboard_focus_and_draft_across_reopen()
    {
        await StaRunner.RunAsync(async () =>
        {
            using var modelHttp = new HttpClient(new OfflineModels());
            var vm = CreateModelViewModel(modelHttp);
            vm.Conversation.SetActiveServant("margin-fixture");
            vm.Conversation.Turns.Add(new("margin-user", FgoPet.Core.Dialogue.ChatMessageRole.User,
                "Could you help me think through my next step?"));
            vm.Conversation.Turns.Add(new("margin-companion", FgoPet.Core.Dialogue.ChatMessageRole.Assistant,
                "Of course. We can take it one step at a time.\nWhat would you like to start with?"));
            var capture = Environment.GetEnvironmentVariable("FGO_PET_ACCEPTANCE_ARTIFACTS");
            var correlation = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Guid.NewGuid().ToByteArray()))[..12];
            void Log(string stage, string result)
            {
                if (string.IsNullOrWhiteSpace(capture)) return;
                Directory.CreateDirectory(capture);
                File.AppendAllText(Path.Combine(capture,"native-scenes.jsonl"),JsonSerializer.Serialize(new
                { timestamp=DateTimeOffset.UtcNow, correlation, stage, result })+Environment.NewLine);
            }
            var storage = Path.Combine(Path.GetTempPath(), "fgopet-margin-web-" + Guid.NewGuid().ToString("N"));
            var factory = new ChatWebSurfaceFactory(new ChatWebSessionFactory(vm.Conversation), storage);
            var workspace = new WorkspaceSelectionStub();
            using var window = new DialogueWindow(vm, webFactory:factory, localWorkspace:workspace);
            Log("entry.dispatch","requested"); window.Show();
            window.Topmost = true;
            var host = Assert.IsType<WebView2SurfaceHost>(((ContentControl)window.FindName("ChatSurface")).Content);
            await WaitUntil(() => Task.FromResult(host.State == WebSurfaceState.Ready));
            Log("session.ready","success");
            var web = Assert.IsType<Microsoft.Web.WebView2.Wpf.WebView2>(host.Children[0]);
            async Task<string> Js(string code) => await web.CoreWebView2.ExecuteScriptAsync(code);
            await web.CoreWebView2.CallDevToolsProtocolMethodAsync("Emulation.setFocusEmulationEnabled", "{\"enabled\":true}");
            await WaitUntil(async () => await Js("!!document.querySelector('button[aria-label=\"展开完整对话\"]')") == "true");
            await Js("document.querySelector('button[aria-label=\"展开完整对话\"]').click()");
            await WaitUntil(async () => await Js("document.querySelectorAll('#messages article').length === 2 && !!document.querySelector('.model-chip')") == "true");
            Assert.Equal(WindowStyle.None, window.WindowStyle);
            Assert.Equal("true", await Js("!!document.querySelector('.chat-header') && document.querySelector('.send-button').disabled"));
            Assert.Equal("true", await Js("document.querySelector('.message-user').getBoundingClientRect().width < document.getElementById('messages').getBoundingClientRect().width && document.documentElement.scrollWidth <= innerWidth"));
            var disabledColor = await Js("getComputedStyle(document.querySelector('.send-button')).backgroundColor");

            await Js("document.querySelector('button[aria-label=\"添加图片与上下文\"]').click()");
            await WaitUntil(async () => await Js("document.querySelectorAll('.context-actions button')[1]?.textContent.includes('Synthetic workspace')") == "true");
            Assert.Equal("true", await Js("!document.querySelectorAll('.context-actions button')[1].disabled && !!document.querySelectorAll('.context-actions button')[1].querySelector('svg')"));
            await Js("document.querySelectorAll('.context-actions button')[1].click()");
            await WaitUntil(() => Task.FromResult(workspace.Choices == 1));
            await Js("[...document.querySelectorAll('.native-links button')].find(b=>b.textContent.includes('清除工作区上下文')).click()");
            await WaitUntil(() => Task.FromResult(workspace.Label is null));
            await WaitUntil(async () => await Js("document.querySelectorAll('.context-actions button')[1].textContent.includes('选择工作区上下文')") == "true");
            await Key(web, "Escape", 27);
            await WaitUntil(async () => await Js("!document.querySelector('.sheet')") == "true");

            // Real keyboard choices travel through the native model owner; the HTTP fixture is offline.
            await Js("document.querySelector('.model-chip').focus()");
            await Key(web, "Enter", 13);
            await WaitUntil(async () => await Js("document.querySelectorAll('.choice-option').length === 2 && document.activeElement.getAttribute('aria-label') === '关闭面板'") == "true");
            await Key(web, "Tab", 9);
            Assert.Equal("true", await Js("document.activeElement === document.querySelectorAll('.choice-option')[0] && document.activeElement.getAttribute('aria-pressed') === 'true'"));
            await Key(web, "Tab", 9);
            Assert.Equal("true", await Js("document.activeElement === document.querySelectorAll('.choice-option')[1]"));
            await Key(web, "Enter", 13);
            await WaitUntil(() => Task.FromResult(vm.Conversation.ModelStatusText == "model-two"));
            await WaitUntil(async () => await Js("!document.querySelector('.sheet') && document.activeElement === document.querySelector('.model-chip')") == "true");
            Assert.Equal("model-two", vm.ModelSelection.SelectedModelId);
            Log("dropdown.selection","success");
            await Key(web, "Enter", 13);
            await WaitUntil(async () => await Js("document.querySelectorAll('.choice-option').length === 2") == "true");
            await Key(web, "Escape", 27);
            await WaitUntil(async () => await Js("!document.querySelector('.sheet') && document.activeElement === document.querySelector('.model-chip')") == "true");
            Assert.True(window.IsVisible); // Escape dismisses the picker, not the native Chat.

            await Js("document.getElementById('composer').focus()");
            await web.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.insertText", "{\"text\":\"offline draft\"}");
            await WaitUntil(() => Task.FromResult(vm.Conversation.InputText == "offline draft"));
            await WaitUntil(async () => await Js("!document.querySelector('.send-button').disabled") == "true");
            Assert.NotEqual(disabledColor, await Js("getComputedStyle(document.querySelector('.send-button')).backgroundColor"));
            window.Close(); Log("window.hidden","success");
            vm.RequestOpen(); Log("window.restored","success");
            Assert.Same(host, ((ContentControl)window.FindName("ChatSurface")).Content);
            Assert.Equal("offline draft", vm.Conversation.InputText);
            Assert.Equal("\"offline draft\"", await Js("document.getElementById('composer').value"));
            // Native expansion changes presentation only; bridge, turns and draft keep their identity.
            window.SetExpanded(true);
            Assert.Equal(WindowStyle.SingleBorderWindow, window.WindowStyle);
            Assert.Equal("offline draft", vm.Conversation.InputText);
            window.SetExpanded(false);
            Assert.Equal(WindowStyle.None, window.WindowStyle);
            Assert.Same(host, ((ContentControl)window.FindName("ChatSurface")).Content);

            if (!string.IsNullOrWhiteSpace(capture))
            {
                Directory.CreateDirectory(capture);
                foreach (var (name, width, height) in new[] { ("normal",376d,500d), ("left-edge",376d,500d), ("right-edge",376d,500d), ("small-workspace",328d,388d) })
                {
                    window.Width=width; window.Height=height;
                    var handle=new System.Windows.Interop.WindowInteropHelper(window).Handle;
                    var area=System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
                    var dpi=System.Windows.Media.VisualTreeHelper.GetDpi(window);
                    window.Left=name=="left-edge" ? area.Left/dpi.DpiScaleX+16
                        : name=="right-edge" ? area.Right/dpi.DpiScaleX-width-16
                        : area.Left/dpi.DpiScaleX+(area.Width/dpi.DpiScaleX-width)/2;
                    window.Top=area.Top/dpi.DpiScaleY+16;
                    window.Activate();
                    await Task.Delay(150);
                    Assert.Equal("true", await Js("document.documentElement.scrollWidth <= innerWidth && document.getElementById('composer').getBoundingClientRect().bottom <= innerHeight"));
                    using var file = File.Create(Path.Combine(capture, name + "-webview.png"));
                    await web.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png,file);
                    // Capture only this opaque offline test window; no unrelated desktop content.
                    Assert.True(GetWindowRect(handle,out var bounds));
                    // Desktop capture is opt-in and requires this fixture to own foreground.
                    // WebView preview remains safe when the user is working in another app.
                    if (Environment.GetEnvironmentVariable("FGO_PET_CAPTURE_NATIVE") == "1" && GetForegroundWindow() == handle)
                    {
                        using var bitmap=new System.Drawing.Bitmap(bounds.Right-bounds.Left,bounds.Bottom-bounds.Top);
                        using(var graphics=System.Drawing.Graphics.FromImage(bitmap))
                            graphics.CopyFromScreen(bounds.Left,bounds.Top,0,0,bitmap.Size);
                        bitmap.Save(Path.Combine(capture,name+"-native.png"),System.Drawing.Imaging.ImageFormat.Png);
                    }
                    await File.WriteAllTextAsync(Path.Combine(capture,name+"-environment.json"),JsonSerializer.Serialize(new
                    {
                        setting="isolated offline native Chat; no character or pending fixture",
                        dpiX=dpi.DpiScaleX,dpiY=dpi.DpiScaleY,monitorCount=System.Windows.Forms.Screen.AllScreens.Length,
                        left=bounds.Left,top=bounds.Top,width=bounds.Right-bounds.Left,height=bounds.Bottom-bounds.Top,
                    }));
                    Log("scene."+name,"captured");
                }
            }
            // The new secondary entry still invokes the original conversation owner through the bridge.
            await Js("document.querySelector('button[aria-label=\"对话历史\"]').click()");
            await WaitUntil(async () => await Js("!!document.querySelector('.new-conversation-button')") == "true");
            await Js("document.querySelector('.new-conversation-button').click()");
            await WaitUntil(() => Task.FromResult(vm.Conversation.Turns.Count == 0));
            Assert.Empty(vm.Conversation.InputText);
            await WaitUntil(async () => await Js("document.querySelectorAll('#messages article').length === 0 && document.getElementById('composer').value === ''") == "true");
            Log("conversation.new","success");
            vm.Conversation.Dispose();
        }, TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task Composer_grows_with_native_drafts_preserves_reading_and_stays_inside_small_window()
    {
        await StaRunner.RunAsync(async () =>
        {
            var vm = DialogueWindowIntegrationTests.CreateViewModel();
            vm.Conversation.SetActiveServant("composer-fixture");
            for (var i = 0; i < 20; i++)
                vm.Conversation.Turns.Add(new($"synthetic-{i}", FgoPet.Core.Dialogue.ChatMessageRole.Assistant,
                    $"Synthetic turn {i}\nEnglish and 中文\nOne more line for the reading anchor."));
            var storage = Path.Combine(Path.GetTempPath(), "fgopet-composer-" + Guid.NewGuid().ToString("N"));
            var factory = new ChatWebSurfaceFactory(new ChatWebSessionFactory(vm.Conversation), storage);
            using var window = new DialogueWindow(vm, webFactory: factory);
            window.Show();
            var host = Assert.IsType<WebView2SurfaceHost>(((ContentControl)window.FindName("ChatSurface")).Content);
            await WaitUntil(() => Task.FromResult(host.State == WebSurfaceState.Ready));
            var web = Assert.IsType<Microsoft.Web.WebView2.Wpf.WebView2>(host.Children[0]);
            async Task<string> Js(string code) => await web.CoreWebView2.ExecuteScriptAsync(code);
            await web.CoreWebView2.CallDevToolsProtocolMethodAsync("Emulation.setFocusEmulationEnabled", "{\"enabled\":true}");
            await WaitUntil(async () => await Js("!!document.querySelector('button[aria-label=\"展开完整对话\"]')") == "true");
            await Js("document.querySelector('button[aria-label=\"展开完整对话\"]').click()");
            await WaitUntil(async () => await Js("!document.getElementById('composer').disabled && document.querySelectorAll('#messages article').length === 20") == "true");
            await WaitUntil(async () => await Js("document.getElementById('messages').getAnimations({subtree:true}).every(a => a.playState === 'finished')") == "true");
            await Js("window.c=document.getElementById('composer');window.s=document.getElementById('message-scroll');window.initialHeight=c.getBoundingClientRect().height;s.focus()");
            await Key(web, "PageUp", 33); // Trusted reading intent pauses follow; script scroll alone does not.
            await Task.Delay(150);
            await Js("s.scrollTo({top:450,behavior:'instant'})");
            await WaitUntil(async () => await Js("!!document.querySelector('.scroll-latest') && Math.abs(s.scrollTop-450)<2") == "true");
            await Task.Delay(100); // Let the browser deliver the reading scroll event before changing layout.
            await Js("window.readingTop=s.scrollTop;c.focus()");
            await web.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.insertText", JsonSerializer.Serialize(new { text = "First line\n第二行\nThird line" }));
            await WaitUntil(() => Task.FromResult(vm.Conversation.InputText == "First line\n第二行\nThird line"));
            try { await WaitUntil(async () => await Js("c.getBoundingClientRect().height > initialHeight && c.getBoundingClientRect().height <= 112 && Math.abs(s.scrollTop-readingTop) < 2") == "true"); }
            catch
            {
                Console.WriteLine("COMPOSER_LAYOUT_METADATA_ONLY " + await Js("JSON.stringify({height:c.getBoundingClientRect().height,initialHeight,top:s.scrollTop,readingTop,viewport:innerHeight,scrollHeight:s.scrollHeight,clientHeight:s.clientHeight})"));
                throw;
            }
            vm.Conversation.Turns[^1].Text += string.Concat(Enumerable.Repeat("\nSynthetic streamed line", 100));
            await Task.Delay(100);
            Assert.Equal("true", await Js("Math.abs(s.scrollTop-readingTop) < 2"));
            await Js("c.select()");
            await web.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.insertText", JsonSerializer.Serialize(new { text = string.Concat(Enumerable.Repeat("Synthetic paste\n", 40)) }));
            await WaitUntil(async () => await Js("c.getBoundingClientRect().height > 106 && c.getBoundingClientRect().height <= 112 && c.scrollHeight > c.clientHeight") == "true");
            window.Width = 328; window.Height = 388;
            try { await WaitUntil(async () => await Js("c.getBoundingClientRect().height <= innerHeight*.55 && s.getBoundingClientRect().height >= 72 && c.getBoundingClientRect().bottom <= innerHeight") == "true"); }
            catch
            {
                Console.WriteLine("COMPOSER_SMALL_LAYOUT_METADATA_ONLY " + await Js("JSON.stringify({height:c.getBoundingClientRect().height,bottom:c.getBoundingClientRect().bottom,readingHeight:s.getBoundingClientRect().height,viewport:innerHeight,width:innerWidth,overflow:document.documentElement.scrollWidth})"));
                throw;
            }
            Assert.Equal("true", await Js("(()=>{const b=document.querySelector('.scroll-latest').getBoundingClientRect(),r=s.getBoundingClientRect();return b.top>=r.top && b.bottom<=r.bottom && b.bottom<=c.getBoundingClientRect().top && document.documentElement.scrollWidth<=innerWidth})()"));
            Assert.Equal("true", await Js("[...document.querySelectorAll('.composer button')].every(b=>{const r=b.getBoundingClientRect();return r.width>0 && r.height>0 && r.left>=0 && r.right<=innerWidth && r.top>=0 && r.bottom<=innerHeight})"));
            // A host draft replacement must follow acknowledgement of local typing.
            await WaitUntil(() => Task.FromResult(vm.Conversation.InputText == string.Concat(Enumerable.Repeat("Synthetic paste\n", 40))));
            vm.Conversation.InputText = "Native restored\ndraft\n第三行";
            await WaitUntil(async () => await Js("c.value.startsWith('Native restored') && c.getBoundingClientRect().height > initialHeight && c.getBoundingClientRect().height <= 112") == "true");
            vm.Conversation.InputText = "";
            await WaitUntil(async () => await Js("c.value === '' && c.getBoundingClientRect().height >= 30 && c.getBoundingClientRect().height <= 40") == "true");
            // Jump resumes following, including a subsequent streamed update.
            await Js("document.querySelector('.scroll-latest').click()");
            await WaitUntil(async () => await Js("s.scrollHeight-s.clientHeight-s.scrollTop<3 && !document.querySelector('.scroll-latest')") == "true");
            vm.Conversation.Turns[^1].Text += "\nFollowed streamed update";
            await WaitUntil(async () => await Js("s.scrollHeight-s.clientHeight-s.scrollTop<3 && !document.querySelector('.scroll-latest')") == "true");
            vm.Conversation.Dispose();
        }, TimeSpan.FromSeconds(60));
    }

    private static async Task Key(Microsoft.Web.WebView2.Wpf.WebView2 web, string key, int virtualKey)
    {
        await web.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", JsonSerializer.Serialize(new
        { type = "keyDown", key, code = key, windowsVirtualKeyCode = virtualKey, text = key == "Enter" ? "\r" : "", unmodifiedText = key == "Enter" ? "\r" : "" }));
        await web.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", JsonSerializer.Serialize(new
        { type = "keyUp", key, code = key, windowsVirtualKeyCode = virtualKey }));
    }

    private static DialogueWindowViewModel CreateModelViewModel(HttpClient http)
    {
        var settings = new OfflineSettings(DialogueSettings.Defaults with
        { ModelConnection = new FgoPet.Core.Settings.ModelConnectionSettings("deepseek", "https://example.test/v1", "model-one") });
        var credentials = new OfflineCredentials();
        var catalog = new ProviderCatalog();
        var connection = new ModelConnectionViewModel(settings, credentials, catalog, new ChatProviderFactory(catalog, credentials, http));
        var database = new RuntimeDatabase(Path.Combine(Path.GetTempPath(), "fgopet-model-web-" + Guid.NewGuid().ToString("N"), "runtime.sqlite3"), pooling: false);
        new RuntimeDatabaseMigrator(database).Migrate();
        var engine = new ConversationOrchestrator(new OfflineProvider(), new OfflineContent(),
            new SqliteConversationRepository(database), new PromptComposer(), TimeProvider.System, settings);
        return new(new ConversationViewModel(engine, settings, connection));
    }

    private sealed class OfflineModels : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.EndsWith("/models", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"data\":[{\"id\":\"model-one\"},{\"id\":\"model-two\"}]}", System.Text.Encoding.UTF8, "application/json") });
        }
    }
    private sealed class OfflineSettings(DialogueSettings initial) : IDialogueSettingsStore
    {
        private DialogueSettings _current = initial;
        public DialogueSettings Load() => _current;
        public void Save(DialogueSettings settings) => _current = settings;
    }
    private sealed class OfflineCredentials : ICredentialStore, ICredentialReader
    {
        public Task SaveAsync(string target, string secret, CancellationToken cancellationToken) => throw new InvalidOperationException("No credential writes in this fixture.");
        public Task<bool> ExistsAsync(string target, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task DeleteAsync(string target, CancellationToken cancellationToken) => throw new InvalidOperationException("No credential writes in this fixture.");
        public Task<string?> ReadAsync(string target, CancellationToken cancellationToken) => Task.FromResult<string?>("synthetic-offline-key");
    }
    private sealed class OfflineProvider : IChatProviderResolver
    { public IChatProvider Resolve() => throw new InvalidOperationException("No conversation requests in this fixture."); }
    private sealed class OfflineContent : IConversationContentResolver
    {
        public Task<ContentBinding> ResolveAsync(string servantId, CancellationToken cancellationToken) => Task.FromResult(new ContentBinding(
            new ContentContextKey(servantId, "stub.pack", "1.0.0", "default", "1", "1"), null,
            Array.Empty<KnowledgeEntry>(), Array.Empty<string>(), string.Empty, string.Empty));
    }

    internal static async Task WaitUntil(Func<Task<bool>> condition,
        [System.Runtime.CompilerServices.CallerMemberName] string member = "",
        [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
    {
        var end=DateTime.UtcNow.AddSeconds(15);
        var watch=System.Diagnostics.Stopwatch.StartNew();
        var correlation=Guid.NewGuid().ToString("N")[..16];
        var callsite=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(member+":"+line)))[..16];
        var samples=0; var transientPairs=0; var settled=false; var outcome="condition_fault";
        void Log(string phase)
        {
            try { Console.WriteLine("MARGIN_WAIT_METADATA_ONLY "+JsonSerializer.Serialize(new {
                atUtc=DateTimeOffset.UtcNow, correlation, callsite, phase, outcome,
                elapsedMs=watch.Elapsed.TotalMilliseconds, deadlineSeconds=15, samples, transientPairs })); }
            catch (Exception) { /* Test diagnostics cannot replace a condition or assertion failure. */ }
        }
        Log("input_received");
        try
        {
            while (true)
            {
                samples++;
                if (await condition())
                {
                    samples++;
                    if (await condition()) { settled=true; outcome="settled"; break; }
                    transientPairs++; // A resize between the two samples must keep waiting, not fail early.
                }
                if (DateTime.UtcNow >= end) { outcome="deadline"; break; }
                await Task.Delay(30);
            }
            Assert.True(settled, "Margin Web surface did not settle.");
        }
        finally { Log("result"); }
    }
}
