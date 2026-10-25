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

/// <summary>Production React modules and root, with synthetic owners and no external IO.</summary>
public sealed class MarginReactSettingsWebTests
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
                Assert.Equal("2", await Text(web, "String(getComputedStyle(document.querySelector('.settings-connection-layout')).gridTemplateColumns.split(' ').length)"));
                Assert.Contains("modelConnection.refreshModels", owner.Calls);
                Assert.DoesNotContain("modelConnection.save", owner.Calls);
                Assert.Equal("manual-fixture", await Text(web, "document.querySelector('[aria-label=\"模型 ID\"]').value"));
                Assert.Equal("true", await Script(web, "document.querySelector('input[type=password]').autocomplete === 'new-password'"));
                await Script(web, "document.querySelector('[aria-label=查看可用模型]').click()");
                await Wait(async () => await Script(web, "!!document.querySelector('[role=option]')") == "true");
                await Script(web, "document.querySelector('[role=option]').click()");
                await Wait(async () => await Text(web, "document.querySelector('[aria-label=\"模型 ID\"]').value") == "fixture-model");

                await Input(web, "input[type=password]", "synthetic-never-persisted");
                await Wait(() => Task.FromResult(owner.KeyReceived));
                Assert.Equal("false", await Script(web, "document.body.innerText.includes('synthetic-never-persisted') || JSON.stringify(localStorage).includes('synthetic-never-persisted')"));
                owner.FailSave = true;
                await Script(web, "document.querySelector('.settings-primary-button').click()");
                await Wait(async () => await Text(web, "document.querySelector('.settings-footer-feedback').textContent") is var text && text.Contains("Synthetic save failure"));
                Assert.Equal("alert", await Text(web, "document.querySelector('.settings-footer-feedback').getAttribute('role')"));
                Assert.Equal("synthetic-never-persisted", await Text(web, "document.querySelector('input[type=password]').value"));
                Assert.Equal("false", await Script(web, "!!document.querySelector('.settings-footer-feedback b')"));
                owner.FailSave = false;
                await Script(web, "document.querySelector('.settings-primary-button').click()");
                await Wait(async () => await Text(web, "document.querySelector('input[type=password]').value") == "");
                Assert.Contains("已保存", await Text(web, "document.querySelector('.settings-footer-feedback').textContent"));
                await Script(web, "Array.from(document.querySelectorAll('button')).find(b=>b.textContent==='清除已保存密钥').click()");
                await Wait(() => Task.FromResult(owner.Calls.Contains("modelConnection.clearKey")));
                await Ready(web);
                Assert.Equal("true", await Script(web, "Array.from(document.querySelectorAll('button')).find(b=>b.textContent==='清除已保存密钥').disabled"));

                await Script(web, "document.querySelector('.settings-advanced').open=true");
                Assert.Equal("true", await Script(web, "document.querySelector('.settings-advanced input').getBoundingClientRect().height>0"));
                await Script(web, "document.querySelector('.settings-footer-actions .settings-secondary-button').click()");
                await Wait(() => Task.FromResult(owner.Calls.Contains("modelConnection.test")));
                await Ready(web);
                Assert.Equal("2", owner.Calls.Count(c => c == "modelConnection.save").ToString());
                await Wait(async () => await Script(web, "document.fonts.check('14px \"DM Sans\"')") == "true");
                await Capture(web, "model-normal");

                window.Width = 360;
                await Wait(async () => await Script(web, "innerWidth<=360") == "true");
                Assert.Equal("1", await Text(web, "String(getComputedStyle(document.querySelector('.settings-connection-layout')).gridTemplateColumns.split(' ').length)"));
                Assert.Equal("true", await Script(web, "document.documentElement.scrollWidth<=innerWidth && document.querySelector('main').scrollWidth<=document.querySelector('main').clientWidth"));
                await Script(web, "document.querySelector('.settings-primary-button').scrollIntoView({block:'center'})");
                Assert.Equal("true", await Script(web, "(()=>{const r=document.querySelector('.settings-primary-button').getBoundingClientRect();return r.left>=0&&r.right<=innerWidth&&r.top>=0&&r.bottom<=innerHeight})()"));
                await Capture(web, "model-narrow");
                await Script(web, "document.getElementById('settings-back').click()");
                await Wait(async () => await Script(web, "!document.querySelector('.settings-native-entry')") == "true");
                await Script(web, "document.querySelector('[data-page-id=ModelConnection]').click()");
                await Ready(web);
                Assert.Equal("", await Text(web, "document.querySelector('input[type=password]').value"));
                Assert.Equal("fixture-model", await Text(web, "document.querySelector('[aria-label=\"模型 ID\"]').value"));
                Assert.Equal("1", await Script(web, "document.querySelectorAll('link[data-margin-settings-style]').length"));
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
                await Wait(async () => await Script(web, "!!document.querySelector('.settings-native-load[role=alert] button')") == "true");
                Assert.DoesNotContain("modelConnection.save", owner.Calls);
                owner.FailRead = false;
                await Script(web, "document.querySelector('.settings-native-load button').click()");
                await Ready(web);
                Assert.DoesNotContain("modelConnection.save", owner.Calls);
            }
            finally { window.Close(); }
        });
    }

    private static SettingsWebRootFactory Factory(ISettingsWebPage owner) => new(
        new SettingsPageCatalog([new SettingsPageViewFactory("ModelConnection", _ => throw new InvalidOperationException(), "Model", order: 10)]),
        [owner], Path.Combine(Path.GetTempPath(), "margin-react-settings-" + Guid.NewGuid().ToString("N")));
    private static Task<string> Script(WebView2 web, string script) => web.CoreWebView2.ExecuteScriptAsync(script);
    private static async Task<string> Text(WebView2 web, string script) => JsonSerializer.Deserialize<string>(await Script(web, script)) ?? "";
    private static Task Input(WebView2 web, string selector, string value) => Script(web,
        $"(()=>{{const e=document.querySelector({JsonSerializer.Serialize(selector)});Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(e,{JsonSerializer.Serialize(value)});e.dispatchEvent(new Event('input',{{bubbles:true}}));}})()");
    private static Task Ready(WebView2 web) => Wait(async () => await Script(web,
        "!!document.querySelector('.connection-page') && document.querySelector('.settings-native-entry').getAttribute('aria-busy')==='false' && !document.querySelector('.settings-primary-button').disabled") == "true");
    private static async Task Wait(Func<Task<bool>> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (!await condition() && DateTime.UtcNow < until) await Task.Delay(25);
        Assert.True(await condition(), "Synthetic React settings did not settle.");
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
