using System.IO;
using System.Text.Json;
using System.Windows;
using FgoPet.App.Settings;
using FgoPet.Character.Settings;
using FgoPet.Core.Settings;
using FgoPet.UiSdk;
using Microsoft.Web.WebView2.Wpf;
using Xunit;

namespace FgoPet.Windows.Tests.Settings;

public sealed class UserProfileWebTests
{
    [Fact]
    public async Task Web_profile_reads_saves_cancels_resets_and_disposes_without_touching_other_settings()
    {
        await StaRunner.RunAsync(async () =>
        {
            var settings = new Store(CharacterSettings.Defaults with
            {
                UserProfile = new UserProfile("旧名称"), Scale = 0.72,
            });
            var owner = new UserProfileViewModel(settings);
            var factory = new UserProfileWebFactory(settings, owner,
                Path.Combine(Path.GetTempPath(), "fgopet-profile-pilot-" + Guid.NewGuid().ToString("N")));
            var host = factory.CreateView();
            var window = new Window { Content = host, Width = 540, Height = 420,
                ShowInTaskbar = false, Left = -10000, Top = -10000 };
            window.Show();
            await WaitUntil(() => host.State == WebSurfaceState.Ready);
            var web = Assert.Single(host.Children.OfType<WebView2>());
            await WaitUntil(async () => await Script(web, "document.querySelector('#display-name').value") == "旧名称");
            window.Width = 320;
            await WaitUntil(async () => await web.CoreWebView2.ExecuteScriptAsync(
                "document.documentElement.scrollWidth <= document.documentElement.clientWidth") == "true");
            await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('#display-name').value='未保存'; document.querySelector('#cancel').click()");
            Assert.Equal("旧名称", await Script(web, "document.querySelector('#display-name').value"));
            Assert.Equal("旧名称", settings.Load().UserProfile?.DisplayName);

            await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('#display-name').value='新名称'; document.querySelector('#profile-form').requestSubmit()");
            await WaitUntil(() => settings.Load().UserProfile?.DisplayName == "新名称");
            await WaitUntil(async () => await Script(web, "document.querySelector('#display-name').value") == "新名称");
            await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('#display-name').value='键盘取消'; document.querySelector('#display-name').dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',bubbles:true}))");
            Assert.Equal("新名称", await Script(web, "document.querySelector('#display-name').value"));
            Assert.Equal("新名称", settings.Load().UserProfile?.DisplayName);
            await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('#reset').click()");
            await WaitUntil(() => settings.Load().UserProfile is null);
            Assert.Equal(0.72, settings.Load().Scale);
            window.Close();
            await WaitUntil(() => host.State == WebSurfaceState.Closed);
        });
    }

    private static async Task<string> Script(WebView2 web, string code) =>
        JsonSerializer.Deserialize<string>(await web.CoreWebView2.ExecuteScriptAsync(code)) ?? string.Empty;

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(25);
        Assert.True(condition());
    }

    private static async Task WaitUntil(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!await condition() && DateTime.UtcNow < deadline) await Task.Delay(25);
        Assert.True(await condition());
    }

    private sealed class Store(CharacterSettings initial) : ICharacterSettingsStore
    {
        private CharacterSettings _settings = initial;
        public CharacterSettings Load() => _settings;
        public void Save(CharacterSettings settings) => _settings = settings;
    }
}
