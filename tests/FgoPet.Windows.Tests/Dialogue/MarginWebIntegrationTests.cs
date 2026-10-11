using System.IO;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using FgoPet.App.Dialogue;
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
        public bool Choose(Func<string?> nativePicker) => true;
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
            var vm = DialogueWindowIntegrationTests.CreateViewModel();
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
            await WaitUntil(async () => await Js("document.querySelector('#model-select + .margin-select-trigger') !== null") == "true");
            Assert.Equal("true", await Js("document.getElementById('project-select') === null && document.getElementById('refresh-projects') === null"));
            Assert.Equal("true", await Js("!document.getElementById('choose-workspace').hidden && document.getElementById('choose-workspace').getAttribute('aria-label') === 'Choose a workspace for this conversation'"));
            await WaitUntil(async () => await Js("document.getElementById('workspace-label').textContent === 'Synthetic workspace'") == "true");
            Assert.Equal("true", await Js("getComputedStyle(document.querySelector('#choose-workspace svg')).width === '14px'"));
            await Js("document.getElementById('clear-workspace').click()");
            await WaitUntil(async () => await Js("document.getElementById('workspace-selection').hidden") == "true");
            Assert.Null(workspace.Label);
            Assert.Equal("\"rgb(178, 113, 139)\"", await Js("getComputedStyle(document.querySelector('.send-button')).backgroundColor"));
            Assert.Contains("Noto Serif SC", await Js("getComputedStyle(document.getElementById('role-name')).fontFamily"));
            await WaitUntil(async () => await Js("document.querySelectorAll('#messages article').length === 2") == "true");
            Assert.Equal(WindowStyle.None, window.WindowStyle);
            Assert.Equal("true", await Js("document.querySelector('.sheet-top') !== null && document.querySelector('.chat-header') === null"));
            Assert.Equal("\"34px\"", await Js("getComputedStyle(document.getElementById('send-message')).width"));
            Assert.Equal("\"82%\"", await Js("getComputedStyle(document.querySelector('.turn.user')).maxWidth"));
            Assert.Equal("true", await Js("document.getElementById('model-select').closest('details') === document.getElementById('more-menu')"));
            await Js("document.getElementById('more-menu').open=true");
            Assert.Equal("true", await Js("document.getElementById('new-conversation').getBoundingClientRect().width > 150"));
            await Js("document.getElementById('more-menu').open=false");

            // Exercise the adapter using the existing select/change contract; no business state machine.
            // A disposable presentation probe avoids changing the real model selection or host state.
            await Js("var s=document.createElement('select');s.id='keyboard-probe';s.setAttribute('aria-label','Keyboard probe');s.replaceChildren(new Option('One','one'),new Option('Two','two'));s.selectedIndex=0;document.querySelector('.context-bar').append(s);var changed=0;s.addEventListener('change',()=>changed++);");
            await WaitUntil(async () => await Js("document.querySelector('#keyboard-probe + button') !== null") == "true");
            await Js("document.querySelector('#keyboard-probe + button').click()");
            Assert.Equal("true", await Js("document.activeElement.getAttribute('aria-selected') === 'true'"));
            await Js("document.activeElement.dispatchEvent(new KeyboardEvent('keydown',{key:'End',bubbles:true}))");
            Assert.Equal("\"Two\"", await Js("document.activeElement.firstChild.textContent"));
            await Js("document.activeElement.click()");
            Assert.Equal("1", await Js("changed"));
            Log("dropdown.selection","success");
            Assert.Equal("true", await Js("document.activeElement === document.querySelector('#keyboard-probe + button')"));
            await Js("document.activeElement.click();document.activeElement.dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',bubbles:true}))");
            Assert.Equal("0", await Js("document.querySelectorAll('.margin-select-menu').length"));
            Assert.Equal("true", await Js("document.activeElement === document.querySelector('#keyboard-probe + button')"));
            await Js("s.nextElementSibling.remove();s.remove()");
            await Task.Delay(50);
            Assert.True(window.IsVisible); // Escape dismisses the picker, not the native Chat.

            await Js("var c=document.getElementById('composer');c.value='offline draft';c.dispatchEvent(new Event('input',{bubbles:true}))");
            await WaitUntil(() => Task.FromResult(vm.Conversation.InputText == "offline draft"));
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
            await Js("document.getElementById('more-menu').open=true;document.getElementById('new-conversation').click()");
            await WaitUntil(() => Task.FromResult(vm.Conversation.Turns.Count == 0));
            Assert.Empty(vm.Conversation.InputText);
            await WaitUntil(async () => await Js("document.querySelectorAll('#messages article').length === 0 && document.getElementById('composer').value === ''") == "true");
            Log("conversation.new","success");
            vm.Conversation.Dispose();
        }, TimeSpan.FromSeconds(60));
    }

    private static async Task WaitUntil(Func<Task<bool>> condition)
    {
        var end=DateTime.UtcNow.AddSeconds(15);
        while (!await condition() && DateTime.UtcNow < end) await Task.Delay(30);
        Assert.True(await condition(), "Margin Web surface did not settle.");
    }
}
