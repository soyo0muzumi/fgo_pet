using System.Windows;
using System.IO;
using System.Text.Json;
using FgoPet.App.Settings;
using FgoPet.UiSdk;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FgoPet.Windows.Tests.Settings;

public sealed class SettingsWebEntryTests
{
    [Fact]
    public async Task Actual_production_root_navigates_pages_synchronizes_native_routes_and_survives_hide()
    {
        await StaRunner.RunAsync(async () =>
        {
            var services = new ServiceCollection();
            FgoPet.App.Bootstrap.ServiceRegistration.AddFgoPet(services, []);
            await using var provider = services.BuildServiceProvider();
            var vm = provider.GetRequiredService<SettingsViewModel>();
            vm.Select(SettingsSection.RolePackages);
            var window = provider.GetRequiredService<SettingsWindow>();
            window.ShowInTaskbar = false;
            window.Left = window.Top = -10000;
            window.Show();
            var host = Assert.IsType<WebView2SurfaceHost>(window.Content);
            await WaitUntil(() => Task.FromResult(host.State == WebSurfaceState.Ready));
            var web = Assert.IsType<Microsoft.Web.WebView2.Wpf.WebView2>(host.Children[0]);
            async Task PageReady(string id) => await WaitUntil(async () =>
                await web.CoreWebView2.ExecuteScriptAsync(
                    "document.querySelector('.navigation-item[aria-current=page]')?.dataset.pageId === "
                    + JsonSerializer.Serialize(id)
                    + " && document.getElementById('page-content').children.length > 0 && !document.getElementById('root-status').textContent") == "true");
            await PageReady("RolePackages");
            foreach (var id in new[] { "UserProfile", "Personalization", "ModelConnection", "Speech",
                         "ConversationMemory", "Privacy", "Theme", "RolePackages" })
            {
                await web.CoreWebView2.ExecuteScriptAsync(
                    "document.querySelector('.navigation-item[data-page-id=" + id + "]').click()");
                await PageReady(id);
                Assert.Equal(id, vm.SelectedPageId);
                Assert.Equal("1", await web.CoreWebView2.ExecuteScriptAsync("document.querySelectorAll('main h1').length"));
                Assert.Equal("true", await web.CoreWebView2.ExecuteScriptAsync(
                    "document.documentElement.scrollWidth <= document.documentElement.clientWidth"));
                var captureRoot = Environment.GetEnvironmentVariable("FGO_PET_ACCEPTANCE_ARTIFACTS");
                if (!string.IsNullOrWhiteSpace(captureRoot))
                {
                    Directory.CreateDirectory(captureRoot);
                    using var file = File.Create(Path.Combine(captureRoot, id + ".png"));
                    await web.CoreWebView2.CapturePreviewAsync(
                        Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, file);
                }
            }
            Assert.Equal("true", await web.CoreWebView2.ExecuteScriptAsync(
                "document.querySelector('.navigation-item[data-page-id=AgentConnection]').disabled"));
            vm.Select(SettingsSection.UserProfile);
            window.NavigateToSelectedPage();
            await PageReady("UserProfile");
            await web.CoreWebView2.ExecuteScriptAsync(
                "var nameInput=document.getElementById('profile-display-name');nameInput.value='未保存的查验草稿';nameInput.dispatchEvent(new Event('input',{bubbles:true}))");
            window.Close();
            Assert.False(window.IsVisible);
            Assert.Equal(WebSurfaceState.Ready, host.State);
            window.Show();
            await PageReady("UserProfile");
            Assert.Equal("未保存的查验草稿", JsonSerializer.Deserialize<string>(
                await web.CoreWebView2.ExecuteScriptAsync("document.getElementById('profile-display-name').value")));
            window.Close();
            vm.Select(SettingsSection.Theme);
            window.NavigateToSelectedPage();
            window.Show();
            await PageReady("Theme");
            Assert.Same(host, window.Content);
            window.Width = 320;
            await WaitUntil(async () => await web.CoreWebView2.ExecuteScriptAsync(
                "innerWidth <= 320 && document.documentElement.scrollWidth <= innerWidth") == "true");
            window.Dispose();
            Assert.Equal(WebSurfaceState.Closed, host.State);
            vm.Select(SettingsSection.UserProfile);
        });
    }

    private static async Task WaitUntil(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!await condition() && DateTime.UtcNow < deadline) await Task.Delay(30);
        Assert.True(await condition(), "The production Settings Web root did not settle.");
    }

    [Fact]
    public async Task Production_settings_entry_uses_one_Web_root_and_registers_all_available_page_owners()
    {
        await StaRunner.RunAsync(async () =>
        {
            var services = new ServiceCollection();
            FgoPet.App.Bootstrap.ServiceRegistration.AddFgoPet(services, [], includeAgentBackend: false);
            await using var provider = services.BuildServiceProvider();
            var window = provider.GetRequiredService<SettingsWindow>();
            Assert.IsType<WebView2SurfaceHost>(window.Content);
            var owners = provider.GetServices<ISettingsWebPage>().Select(page => page.SettingsPageId).ToArray();
            Assert.Equal(8, owners.Length);
            foreach (var id in new[] { "UserProfile", "Personalization", "ModelConnection", "RolePackages",
                         "Speech", "ConversationMemory", "Privacy", "Theme" }) Assert.Contains(id, owners);
            Assert.Equal(owners.Length, owners.Distinct(StringComparer.Ordinal).Count());
        });
    }
}
