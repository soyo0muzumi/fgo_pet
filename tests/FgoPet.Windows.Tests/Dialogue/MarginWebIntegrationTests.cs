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
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);
    [Fact]
    public async Task Paper_menu_keeps_owner_commands_keyboard_focus_and_draft_across_reopen()
    {
        await StaRunner.RunAsync(async () =>
        {
            var vm = DialogueWindowIntegrationTests.CreateViewModel();
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
            using var window = new DialogueWindow(vm, webFactory:factory);
            Log("entry.dispatch","requested"); window.Show();
            window.Topmost = true;
            var host = Assert.IsType<WebView2SurfaceHost>(((ContentControl)window.FindName("ChatSurface")).Content);
            await WaitUntil(() => Task.FromResult(host.State == WebSurfaceState.Ready));
            Log("session.ready","success");
            var web = Assert.IsType<Microsoft.Web.WebView2.Wpf.WebView2>(host.Children[0]);
            async Task<string> Js(string code) => await web.CoreWebView2.ExecuteScriptAsync(code);
            await WaitUntil(async () => await Js("document.querySelectorAll('.margin-select-trigger').length === 2") == "true");
            Assert.Equal("\"rgb(178, 113, 139)\"", await Js("getComputedStyle(document.querySelector('.send-button')).backgroundColor"));
            Assert.Contains("Noto Serif SC", await Js("getComputedStyle(document.getElementById('role-name')).fontFamily"));

            // Exercise the adapter using the existing select/change contract; no business state machine.
            await Js("var s=document.getElementById('model-select');s.replaceChildren(new Option('One','one'),new Option('Two','two'));s.disabled=false;s.selectedIndex=0;var changed=0;s.addEventListener('change',()=>changed++);");
            await WaitUntil(async () => await Js("document.querySelector('#model-select + button').disabled === false") == "true");
            await Js("document.querySelector('#model-select + button').click()");
            Assert.Equal("true", await Js("document.activeElement.getAttribute('aria-selected') === 'true'"));
            await Js("document.activeElement.dispatchEvent(new KeyboardEvent('keydown',{key:'End',bubbles:true}))");
            Assert.Equal("\"Two\"", await Js("document.activeElement.firstChild.textContent"));
            await Js("document.activeElement.click()");
            Assert.Equal("1", await Js("changed"));
            Log("dropdown.selection","success");
            Assert.Equal("true", await Js("document.activeElement === document.querySelector('#model-select + button')"));
            await Js("document.activeElement.click();document.activeElement.dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',bubbles:true}))");
            Assert.Equal("0", await Js("document.querySelectorAll('.margin-select-menu').length"));
            Assert.Equal("true", await Js("document.activeElement === document.querySelector('#model-select + button')"));

            await Js("var c=document.getElementById('composer');c.value='offline draft';c.dispatchEvent(new Event('input',{bubbles:true}))");
            await WaitUntil(() => Task.FromResult(vm.Conversation.InputText == "offline draft"));
            window.Close(); Log("window.hidden","success");
            vm.RequestOpen(); Log("window.restored","success");
            Assert.Same(host, ((ContentControl)window.FindName("ChatSurface")).Content);
            Assert.Equal("offline draft", vm.Conversation.InputText);
            Assert.Equal("\"offline draft\"", await Js("document.getElementById('composer').value"));

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
                    using var bitmap=new System.Drawing.Bitmap(bounds.Right-bounds.Left,bounds.Bottom-bounds.Top);
                    using(var graphics=System.Drawing.Graphics.FromImage(bitmap))
                        graphics.CopyFromScreen(bounds.Left,bounds.Top,0,0,bitmap.Size);
                    bitmap.Save(Path.Combine(capture,name+"-native.png"),System.Drawing.Imaging.ImageFormat.Png);
                    await File.WriteAllTextAsync(Path.Combine(capture,name+"-environment.json"),JsonSerializer.Serialize(new
                    {
                        setting="isolated offline native Chat; no character or pending fixture",
                        dpiX=dpi.DpiScaleX,dpiY=dpi.DpiScaleY,monitorCount=System.Windows.Forms.Screen.AllScreens.Length,
                        left=bounds.Left,top=bounds.Top,width=bounds.Right-bounds.Left,height=bounds.Bottom-bounds.Top,
                    }));
                    Log("scene."+name,"captured");
                }
            }
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
