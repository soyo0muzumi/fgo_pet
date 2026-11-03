using System.IO;
using System.Text.Json;
using System.Windows;
using FgoPet.App.Settings;
using FgoPet.Extensibility;
using FgoPet.UiSdk;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Xunit;

namespace FgoPet.Windows.Tests.Settings;

/// <summary>Production model settings module and root, with synthetic owners and no external IO.</summary>
public sealed class ModelConnectionSettingsWebTests
{
    [Fact]
    public async Task Connection_commands_preserve_drafts_report_failure_and_clear_write_only_keys()
    {
        await StaRunner.RunAsync(async () =>
        {
            var owner = new ModelOwner();
            using var host = Factory(owner).CreateView("ModelConnection");
            var window = new Window { Content = host, Width = 1050, Height = 760, ShowInTaskbar = false, Left = -10000, Top = -10000 };
            try
            {
                window.Show();
                await Wait(() => Task.FromResult(host.State == WebSurfaceState.Ready));
                var web = Assert.Single(host.Children.OfType<WebView2>());
                await Ready(web);
                Assert.Equal("2", await Text(web, "String(getComputedStyle(document.querySelector('.mc-fields')).gridTemplateColumns.split(' ').length)"));
                Assert.Contains("modelConnection.refreshModels", owner.Calls);
                Assert.DoesNotContain("modelConnection.save", owner.Calls);
                Assert.Equal("manual-fixture", await Text(web, "document.querySelector('[data-field=modelId]').value"));
                Assert.Equal("true", await Script(web, "document.querySelector('input[type=password]').autocomplete === 'new-password'"));
                await Script(web, "document.querySelector('[data-model-toggle]').click()");
                await Wait(async () => await Script(web, "!!document.querySelector('[data-model-id]')") == "true");
                await Script(web, "document.querySelector('[data-model-id]').click()");
                await Wait(async () => await Text(web, "document.querySelector('[data-field=modelId]').value") == "fixture-model");

                await Input(web, "input[type=password]", "synthetic-never-persisted");
                await Wait(() => Task.FromResult(owner.KeyReceived));
                Assert.Equal("false", await Script(web, "document.body.innerText.includes('synthetic-never-persisted') || JSON.stringify(localStorage).includes('synthetic-never-persisted')"));
                owner.FailSave = true;
                await Script(web, "document.querySelector('[data-save]').click()");
                await Wait(async () => await Text(web, "document.querySelector('.mc-error').textContent") is var text && text.Contains("Synthetic save failure"));
                Assert.Equal("alert", await Text(web, "document.querySelector('.mc-error').getAttribute('role')"));
                Assert.Equal("synthetic-never-persisted", await Text(web, "document.querySelector('input[type=password]').value"));
                Assert.Equal("false", await Script(web, "!!document.querySelector('.mc-error b')"));
                owner.FailSave = false;
                await Script(web, "document.querySelector('[data-save]').click()");
                await Wait(async () => await Text(web, "document.querySelector('input[type=password]').value") == "");
                Assert.Contains("Saved", await Text(web, "document.querySelector('[data-status]').textContent"));
                await Script(web, "document.querySelector('[data-clear-key]').click()");
                await Wait(() => Task.FromResult(owner.Calls.Contains("modelConnection.clearKey")));
                await Ready(web);
                Assert.Equal("true", await Script(web, "document.querySelector('[data-clear-key]').disabled"));

                await Script(web, "document.querySelector('.mc-advanced').open=true");
                Assert.Equal("true", await Script(web, "document.querySelector('.mc-advanced input').getBoundingClientRect().height>0"));
                await Script(web, "document.querySelector('[data-test]').click()");
                await Wait(() => Task.FromResult(owner.Calls.Contains("modelConnection.test")));
                await Ready(web);
                Assert.Equal("2", owner.Calls.Count(c => c == "modelConnection.save").ToString());

                await Capture(web, "model-normal");

                window.Width = 360;
                await Wait(async () => await Script(web, "innerWidth<=360") == "true");
                Assert.Equal("1", await Text(web, "String(getComputedStyle(document.querySelector('.mc-fields')).gridTemplateColumns.split(' ').length)"));
                Assert.Equal("true", await Script(web, "document.documentElement.scrollWidth<=innerWidth && document.querySelector('main').scrollWidth<=document.querySelector('main').clientWidth"));
                await Script(web, "document.querySelector('[data-save]').scrollIntoView({block:'center'})");
                Assert.Equal("true", await Script(web, "(()=>{const r=document.querySelector('[data-save]').getBoundingClientRect();return r.left>=0&&r.right<=innerWidth&&r.top>=0&&r.bottom<=innerHeight})()"));
                await Capture(web, "model-narrow");
                await Navigate(web, "UserProfile");
                await Wait(async () => await Script(web, "!!document.querySelector('#profile-display-name')") == "true");
                await Navigate(web, "ModelConnection");
                await Ready(web);
                Assert.Equal("", await Text(web, "document.querySelector('input[type=password]').value"));
                Assert.Equal("fixture-model", await Text(web, "document.querySelector('[data-field=modelId]').value"));
                Assert.Equal("1", await Script(web, "document.querySelectorAll('link[data-settings-owner=model-connection]').length"));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task Failed_initial_read_can_retry_without_dispatching_save()
    {
        await StaRunner.RunAsync(async () =>
        {
            var owner = new ModelOwner { FailRead = true };
            using var host = Factory(owner).CreateView("ModelConnection");
            var window = new Window { Content = host, Width = 824, Height = 740, ShowInTaskbar = false, Left = -10000, Top = -10000 };
            try
            {
                window.Show();
                await Wait(() => Task.FromResult(host.State == WebSurfaceState.Ready));
                var web = Assert.Single(host.Children.OfType<WebView2>());
                await Wait(async () => await Script(web, "document.querySelector('.mc-page')?.dataset.state==='read-error' && !document.querySelector('[data-retry]').hidden") == "true");
                Assert.DoesNotContain("modelConnection.save", owner.Calls);
                owner.FailRead = false;
                await Script(web, "document.querySelector('[data-retry]').click()");
                await Ready(web);
                Assert.DoesNotContain("modelConnection.save", owner.Calls);
            }
            finally { window.Close(); }
        });
    }

    private static SettingsWebRootFactory Factory(ISettingsWebPage owner) => new(
        new SettingsPageCatalog([new SettingsPageViewFactory("ModelConnection", _ => throw new InvalidOperationException(), "Model", order: 10),
            new SettingsPageViewFactory("UserProfile", _ => throw new InvalidOperationException(), "Profile", order: 20)]),
        [owner, new ProfileOwner()], Path.Combine(Path.GetTempPath(), "margin-react-settings-" + Guid.NewGuid().ToString("N")));
    private static Task<string> Script(WebView2 web, string script) => web.CoreWebView2.ExecuteScriptAsync(script);
    private static async Task<string> Text(WebView2 web, string script) => JsonSerializer.Deserialize<string>(await Script(web, script)) ?? "";
    private static Task Input(WebView2 web, string selector, string value) => Script(web,
        $"(()=>{{const e=document.querySelector({JsonSerializer.Serialize(selector)});Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(e,{JsonSerializer.Serialize(value)});e.dispatchEvent(new Event('input',{{bubbles:true}}));}})()");
    private static Task Ready(WebView2 web) => Wait(async () => await Script(web,
        "document.querySelector('.mc-page')?.dataset.state === 'ready'") == "true");
    private static Task Navigate(WebView2 web, string pageId) => Script(web,
        $"(()=>{{const select=document.getElementById('settings-mobile-page');select.value={JsonSerializer.Serialize(pageId)};select.dispatchEvent(new Event('change',{{bubbles:true}}));}})()");
    private sealed class ProfileOwner : ISettingsWebPage
    {
        public string SettingsPageId => "UserProfile";
        public string ModulePath => "pages/user-profile.js";
        public IReadOnlyList<string> Commands => ["getProfile", "saveProfile", "resetProfile"];
        public ValueTask<WebSurfaceCommandResult> HandleCommandAsync(WebSurfaceMessage message, CancellationToken token)
            => ValueTask.FromResult(new WebSurfaceCommandResult(true, new { displayName = "Fixture" }));
    }
    private static async Task Wait(Func<Task<bool>> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (!await condition() && DateTime.UtcNow < until) await Task.Delay(25);
        Assert.True(await condition(), "Synthetic model settings did not settle.");
    }
    private static async Task Capture(WebView2 web, string scene)
    {
        if (Environment.GetEnvironmentVariable("FGO_PET_MARGIN_SERVICE_CAPTURE_DIR") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory);
        using var file = File.Create(Path.Combine(directory, scene + ".png"));
        await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, file);
    }
    private sealed class ModelOwner : ISettingsWebPage
    {
        public string SettingsPageId => "ModelConnection";
        public string ModulePath => "pages/model-connection.js";
        public IReadOnlyList<string> Commands => ["modelConnection.get", "modelConnection.setDraft", "modelConnection.setShowReasoning", "modelConnection.test", "modelConnection.refreshModels", "modelConnection.save", "modelConnection.clearKey"];
        public List<string> Calls { get; } = [];
        public bool FailSave { get; set; }
        public bool FailRead { get; set; }
        public bool KeyReceived { get; private set; }
        private readonly Dictionary<string, object?> snapshot = new()
        {
            ["providerId"] = "fixture", ["providers"] = new[] { new { id = "fixture", displayName = "Fixture", defaultBaseUrl = "https://example.invalid/v1" } },
            ["baseUrl"] = "https://example.invalid/v1", ["modelId"] = "manual-fixture",
            ["availableModels"] = new[] { new { id = "fixture-model", displayName = "Fixture model" } },
            ["contextWindowOverrideText"] = "", ["maxOutputTokensText"] = "2048", ["contextLimitText"] = "", ["providerStatusText"] = "", ["modelStatusText"] = "",
            ["keyStateText"] = "已保存密钥", ["isKeySaved"] = true, ["showReasoning"] = false, ["isBusy"] = false, ["statusText"] = "", ["errorText"] = "",
        };
        public ValueTask<WebSurfaceCommandResult> HandleCommandAsync(WebSurfaceMessage message, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Calls.Add(message.Type);
            var failed = message.Type == "modelConnection.save" && FailSave || message.Type == "modelConnection.get" && FailRead;
            if (Environment.GetEnvironmentVariable("FGO_PET_MARGIN_SERVICE_CAPTURE_DIR") is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, "react-settings.jsonl"), JsonSerializer.Serialize(new {
                    time = DateTimeOffset.UtcNow, request = "synthetic-settings", stage = message.Type,
                    result = failed ? "failed" : "confirmed", errorCode = failed ? "SETTINGS_UNAVAILABLE" : null }) + Environment.NewLine);
            }
            if (message.Type == "modelConnection.get" && FailRead) return ValueTask.FromResult(new WebSurfaceCommandResult(false, ErrorCode: "SETTINGS_UNAVAILABLE"));
            snapshot["errorText"] = "";
            if (message.Type == "modelConnection.setDraft")
            {
                var field = message.Payload.GetProperty("field").GetString()!;
                if (field == "apiKey") KeyReceived = true;
                else snapshot[field] = message.Payload.GetProperty("value").GetString();
            }
            if (message.Type == "modelConnection.save")
            {
                if (FailSave)
                {
                    snapshot["errorText"] = "Synthetic save failure <b>literal</b>";
                    return ValueTask.FromResult(new WebSurfaceCommandResult(false, new { snapshot }, "SETTINGS_SAVE_FAILED"));
                }
                snapshot["statusText"] = "已保存 · Fixture";
            }
            if (message.Type == "modelConnection.clearKey") snapshot["isKeySaved"] = false;
            return ValueTask.FromResult(new WebSurfaceCommandResult(true, snapshot));
        }
    }
}
