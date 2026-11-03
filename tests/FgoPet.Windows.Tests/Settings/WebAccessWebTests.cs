using FgoPet.App.Settings;
using FgoPet.Extensibility;
using FgoPet.Plugin.Browser;
using FgoPet.Plugin.WebSearch;
using FgoPet.UiSdk;
using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Xunit;

namespace FgoPet.Windows.Tests.Settings;

public sealed class WebAccessWebTests
{
    [Fact]
    public async Task Real_page_keeps_permissions_explicit_recovers_drafts_and_fits_a_narrow_window()
    {
        await StaRunner.RunAsync(async () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "fgopet-network-ui-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var worker = Path.Combine(root, "synthetic-worker.exe");
            File.WriteAllText(worker, "synthetic file; never executed");
            var searchPath = Path.Combine(root, "search.json");
            var browserPath = Path.Combine(root, "browser.json");
            var checks = 0;
            ValueTask<ToolAvailability> Check(CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                checks++;
                return ValueTask.FromResult(new ToolAvailability(true));
            }
            var owner = new WebAccessSettingsWebPage(new(searchPath, worker), new(browserPath), Check, Check, Check, root);
            var catalog = new SettingsPageCatalog([
                new SettingsPageViewFactory("WebAccess", _ => throw new InvalidOperationException(), "Web access"),
                new SettingsPageViewFactory("UserProfile", _ => throw new InvalidOperationException(), "Profile")]);
            using var host = new SettingsWebRootFactory(catalog, [owner, new ProfileOwner()], Path.Combine(root, "webview"))
                .CreateView("WebAccess");
            var window = new Window { Content = host, Width = 1000, Height = 780, ShowInTaskbar = false, Left = -10000, Top = -10000 };
            try
            {
                window.Show();
                await Wait(() => Task.FromResult(host.State == WebSurfaceState.Ready));
                var web = Assert.Single(host.Children.OfType<WebView2>());
                await Ready(web);
                Assert.False(File.Exists(searchPath));
                Assert.False(File.Exists(browserPath));
                Assert.Equal(0, checks);
                Assert.Equal("true", await Script(web, "!document.querySelector('input[type=password]') && document.querySelectorAll('main h1').length===1"));
                await Script(web, "document.querySelector('[data-search-toggle]').click()");
                await Wait(() => Task.FromResult(WebSearchSettings.Load(searchPath)?.Enabled == true));
                await Ready(web);
                Assert.Equal(0, checks);
                await Script(web, "document.querySelector('[data-search-test]').click()");
                await Wait(() => Task.FromResult(checks == 1));
                await Ready(web);
                await Script(web, "document.querySelector('[data-search-toggle]').click()");
                await Wait(() => Task.FromResult(WebSearchSettings.Load(searchPath) is null));
                await Ready(web);
                await Script(web, "document.querySelector('.web-access-advanced').open=true; const input=document.querySelector('[name=nodePath]'); input.value='relative-invalid-path'; input.dispatchEvent(new Event('input',{bubbles:true})); document.querySelector('[data-browser-save]').click()");
                await Wait(async () => await Script(web, "!document.querySelector('[data-error]').hidden && document.querySelector('.web-access-page').getAttribute('aria-busy')==='false'") == "true");
                Assert.False(File.Exists(browserPath));
                Assert.Equal("\"relative-invalid-path\"", await Script(web, "document.querySelector('[name=nodePath]').value"));
                await Navigate(web, "UserProfile");
                await Wait(async () => await Script(web, "!!document.getElementById('profile-display-name')") == "true");
                await Navigate(web, "WebAccess");
                await Ready(web);
                Assert.Equal("true", await Script(web, "document.querySelector('.web-access-advanced').open && document.querySelector('[name=nodePath]').value==='relative-invalid-path' && !document.querySelector('[data-dirty]').hidden"));
                window.Width = 360;
                await Wait(async () => await Script(web, "innerWidth<=360 && document.documentElement.scrollWidth<=innerWidth") == "true");
                await Script(web, "document.querySelector('[data-browser-save]').scrollIntoView({block:'center'})");
                Assert.Equal("true", await Script(web, "(()=>{const r=document.querySelector('[data-browser-save]').getBoundingClientRect();return r.left>=0&&r.right<=innerWidth&&r.top>=0&&r.bottom<=innerHeight})()"));
                if (Environment.GetEnvironmentVariable("FGO_PET_ACCEPTANCE_ARTIFACTS") is { Length: > 0 } directory)
                {
                    Directory.CreateDirectory(directory);
                    using var capture = File.Create(Path.Combine(directory, "WebAccess-narrow.png"));
                    await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, capture);
                }
                Assert.Equal(1, checks);
            }
            finally { window.Close(); }
        });
    }

    private static Task<string> Script(WebView2 web, string script) => web.CoreWebView2.ExecuteScriptAsync(script);
    private static Task Ready(WebView2 web) => Wait(async () => await Script(web,
        "!!document.querySelector('.web-access-page') && document.querySelector('.web-access-page').getAttribute('aria-busy')==='false' && !document.querySelector('[data-search-toggle]').disabled") == "true");
    private static Task Navigate(WebView2 web, string id) => Script(web,
        $"(()=>{{const select=document.getElementById('settings-mobile-page'); select.value={JsonSerializer.Serialize(id)}; select.dispatchEvent(new Event('change',{{bubbles:true}}));}})()");
    private static async Task Wait(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!await condition() && DateTime.UtcNow < deadline) await Task.Delay(25);
        Assert.True(await condition(), "Network settings did not settle.");
    }
    private sealed class ProfileOwner : ISettingsWebPage
    {
        public string SettingsPageId => "UserProfile";
        public string ModulePath => "pages/user-profile.js";
        public IReadOnlyList<string> Commands => ["getProfile", "saveProfile", "resetProfile"];
        public ValueTask<WebSurfaceCommandResult> HandleCommandAsync(WebSurfaceMessage message, CancellationToken token)
            => ValueTask.FromResult(new WebSurfaceCommandResult(true, new { displayName = "Fixture" }));
    }

    [Fact]
    public async Task Production_catalog_includes_the_network_settings_owner()
    {
        await StaRunner.RunAsync(async () =>
        {
            var services = new ServiceCollection();
            FgoPet.App.Bootstrap.ServiceRegistration.AddFgoPet(services, [], includeAgentBackend: false);
            await using var provider = services.BuildServiceProvider();
            var pages = provider.GetServices<ISettingsWebPage>().ToArray();
            Assert.Contains(pages, page => page.SettingsPageId == "WebAccess"
                && page.ModulePath == "pages/web-access.js");
        });
    }
}
