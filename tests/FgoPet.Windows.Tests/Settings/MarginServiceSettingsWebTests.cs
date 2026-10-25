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

/// <summary>Production page modules with synthetic bridge data; never contacts a service or reads credentials.</summary>
public sealed class MarginServiceSettingsWebTests
{
    [Fact]
    public async Task Model_list_loads_on_entry_and_open_and_provider_change_clears_key_input()
    {
        await StaRunner.RunAsync(async () =>
        {
            var owner = new FixtureOwner("ModelConnection");
            using var host = Factory(owner).CreateView("ModelConnection");
            var window = Window(host);
            try
            {
                window.Show();
                await Wait(() => Task.FromResult(host.State == WebSurfaceState.Ready));
                var web = Assert.Single(host.Children.OfType<WebView2>());
                await ReadyModel(web);
                await Wait(() => Task.FromResult(owner.Calls.Contains("modelConnection.refreshModels")));
                await Wait(async () => await Script(web, "!Array.from(document.querySelectorAll('.settings-model-picker button')).some(b=>b.disabled)") == "true");
                var count = owner.Calls.Count(call => call == "modelConnection.refreshModels");
                await Script(web, "document.querySelector('[aria-label=查看可用模型]').click()");
                await Wait(() => Task.FromResult(owner.Calls.Count(call => call == "modelConnection.refreshModels") > count));
                await Wait(async () => await Script(web, "!Array.from(document.querySelectorAll('.settings-model-picker button')).some(b=>b.disabled)") == "true");

                await Script(web, "var key=document.querySelector('input[type=password]');Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(key,'fixture-provider-a');key.dispatchEvent(new Event('input',{bubbles:true}))");
                await ReadyModel(web);
                await Script(web, "Array.from(document.querySelectorAll('.settings-provider-options button')).find(b=>b.textContent==='Local provider').click()");
                await Wait(async () => await Script(web, "document.querySelector('input[type=password]').value==='' && Array.from(document.querySelectorAll('.settings-provider-options button')).some(b=>b.textContent==='Local provider'&&b.getAttribute('aria-pressed')==='true')") == "true");
                await ReadyModel(web);
                Assert.DoesNotContain("modelConnection.test", owner.Calls);
                Assert.DoesNotContain("modelConnection.save", owner.Calls);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData("Speech", "sc")]
    public async Task Known_owner_feedback_is_English_and_unknown_content_remains_literal(string pageId, string prefix)
    {
        await StaRunner.RunAsync(async () =>
        {
            var owner = new FixtureOwner(pageId);
            var cases = prefix == "mc"
                ? new[] { ("未测试连接。", "Connection not tested."),
                    ("测试完成，但草稿已更改；结果未应用。", "Test completed, but the draft changed. The result was not applied."),
                    ("已刷新 2 个模型。", "Refreshed 2 models."),
                    ("连接成功（尚未保存） · Fixture <b>model</b>", "Connection succeeded (not saved) · Fixture <b>model</b>"),
                    ("已保存 · Fixture model", "Saved · Fixture model") }
                : new[] { ("朗读设置已保存；当前保持关闭。", "Voice settings saved. Speech remains off."),
                    ("已停止播放；本地服务可能仍在生成，迟到音频不会播放。", "Playback stopped. The local service may still be generating; late audio will not play."),
                    ("音色记录已删除；恢复备份不包含本机参考音频。", "Voice record deleted. Restored backups do not include local reference audio."),
                    ("试听完成。", "Preview completed.") };
            owner.SetFeedback(cases[0].Item1);
            using var host = Factory(owner).CreateView(pageId);
            var window = Window(host);
            try
            {
                window.Show();
                await Wait(() => Task.FromResult(host.State == WebSurfaceState.Ready));
                var web = Assert.Single(host.Children.OfType<WebView2>());
                await Ready(web, prefix);
                Assert.Equal(cases[0].Item2, await Text(web, "document.querySelector('[data-status]').textContent"));
                foreach (var (source, expected) in cases.Skip(1))
                {
                    owner.SetFeedback(source);
                    await Script(web, "document.querySelector('[data-save]').click()");
                    await Wait(async () => await Text(web, "document.querySelector('[data-status]').textContent") == expected);
                    await Ready(web, prefix);
                }
                Assert.Equal("false", await Script(web, "!!document.querySelector('[data-status] b')"));
                owner.SetFeedback("Fixture status <b>literal</b>", prefix == "mc"
                    ? "最大输出超出模型限制，或没有为输入留下空间。"
                    : "朗读设置无效，请检查地址、语言和文本长度。");
                await Script(web, "document.querySelector('[data-save]').click()");
                await Wait(async () => await Text(web, "document.querySelector('[data-status]').textContent") == "Fixture status <b>literal</b>");
                Assert.Equal(prefix == "mc" ? "Maximum output exceeds the model limit or leaves no room for input."
                    : "Voice settings are invalid. Check the endpoint, language and text length.",
                    await Text(web, "document.querySelector('[data-error]').textContent"));
                Assert.Equal("false", await Script(web, "!!document.querySelector('[data-status] b')"));
                await Script(web, "document.querySelector('[data-status]').scrollIntoView({block:'center'})");
                await Capture(web, prefix + "-owner-feedback");
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData("Speech", "sc")]
    public async Task Service_details_disclose_options_preserve_commands_and_keep_keys_write_only(string pageId, string prefix)
    {
        await StaRunner.RunAsync(async () =>
        {
            var owner = new FixtureOwner(pageId);
            using var host = Factory(owner).CreateView(pageId);
            var window = Window(host);
            try
            {
                window.Show();
                await Wait(() => Task.FromResult(host.State == WebSurfaceState.Ready));
                var web = Assert.Single(host.Children.OfType<WebView2>());
                if (Environment.GetEnvironmentVariable("FGO_PET_SERVICE_TIMING_DIAGNOSTICS") == "1")
                    await InstallTimingTrace(web, prefix);
                await Ready(web, prefix);
                Assert.Equal(pageId == "Speech" ? "Save voice settings" : "Save",
                    await Text(web, "document.querySelector('[data-save]').textContent"));
                Assert.Equal("true", await Script(web, $"!document.querySelector('.{prefix}-advanced').open"));
                Assert.Equal("true", await Script(web, "document.querySelector('[data-field=apiKey]').type === 'password'"));
                Assert.Equal("Services", await Text(web, "document.querySelector('#settings-parent').textContent"));
                await Capture(web, prefix + "-normal");

                await Script(web, $"document.querySelector('.{prefix}-advanced').open=true");
                var advancedField = prefix == "mc" ? "mc-base" : "sc-limit";
                Assert.Equal("true", await Script(web, $"document.getElementById('{advancedField}').getBoundingClientRect().height > 0"));
                await Capture(web, prefix + "-advanced");

                var secret = "fixture-key-never-display";
                await Script(web, "var key=document.querySelector('[data-field=apiKey]');key.value='fixture-key-never-display';key.dispatchEvent(new Event('input',{bubbles:true}))");
                await Wait(() => Task.FromResult(owner.Calls.Contains(owner.CommandPrefix + ".setDraft")));
                await Ready(web, prefix);
                Assert.Equal("false", await Script(web, $"document.body.innerText.includes({JsonSerializer.Serialize(secret)})"));
                await Script(web, "document.querySelector('[data-save]').click()");
                await Wait(() => Task.FromResult(owner.Calls.Contains(owner.CommandPrefix + ".save")));
                await Ready(web, prefix);

                var providerId = prefix + "-provider";
                await Wait(async () => await Script(web, $"!!document.getElementById('{providerId}').nextElementSibling?.classList.contains('margin-select-trigger')") == "true");
                await Script(web, $"document.getElementById('{providerId}').nextElementSibling.click()");
                await Wait(async () => await Script(web, "document.querySelectorAll('.margin-select-menu:not([hidden]) .margin-select-option').length >= 2") == "true", web, "provider-menu-open");
                Assert.Equal("1", await Script(web, "document.querySelectorAll('.margin-select-menu:not([hidden]) [aria-selected=true]').length"));
                await Capture(web, prefix + "-dropdown");
                await Script(web, "document.querySelector('.margin-select-menu:not([hidden]) .margin-select-option').dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',bubbles:true}))");
                await Wait(async () => await Script(web, "!document.querySelector('.margin-select-menu:not([hidden])')") == "true");

                if (prefix == "mc")
                {
                    await Script(web, "document.querySelector('[data-test]').click()");
                    await Wait(() => Task.FromResult(owner.Calls.Contains("modelConnection.test")));
                }
                else
                {
                    await Script(web, "var p=document.getElementById('sc-provider');p.value='indextts';p.dispatchEvent(new Event('change',{bubbles:true}))");
                    await Wait(async () => await Script(web, "!document.querySelector('[data-card=indextts]').hidden") == "true");
                    await Script(web, "document.querySelector('[data-delete-voice]').click()");
                    Assert.Equal("false", await Script(web, "document.querySelector('[data-delete-confirm]').hidden"));
                    Assert.DoesNotContain("speech.deleteVoice", owner.Calls);
                    await Script(web, "document.querySelector('[data-delete-cancel]').click()");
                    Assert.DoesNotContain("speech.deleteVoice", owner.Calls);
                    await Script(web, "document.querySelector('[data-preview]').click()");
                    await Wait(() => Task.FromResult(owner.Calls.Contains("speech.preview")));
                    await Ready(web, prefix);
                    await Script(web, "document.querySelector('[data-stop-preview]').click()");
                    await Wait(() => Task.FromResult(owner.Calls.Contains("speech.stopPreview")));
                }
                await Ready(web, prefix);
                window.Width = 280;
                await Wait(async () => await Script(web, "innerWidth <= 280") == "true");
                Assert.Equal("true", await Script(web, "document.documentElement.scrollWidth <= innerWidth && document.querySelector('main').scrollWidth <= document.querySelector('main').clientWidth"));
                await Capture(web, prefix + "-small-workspace");
                await Script(web, "document.querySelector('[data-save]').scrollIntoView({block:'center'})");
                Assert.Equal("true", await Script(web, "(() => {const r=document.querySelector('[data-save]').getBoundingClientRect(); return r.top >= 0 && r.bottom <= innerHeight && r.left >= 0 && r.right <= innerWidth;})()"));
                await Capture(web, prefix + "-small-actions");
                await Script(web, $"document.getElementById('{advancedField}').scrollIntoView({{block:'center'}})");
                Assert.Equal("true", await Script(web, $"(() => {{const r=document.getElementById('{advancedField}').getBoundingClientRect(); return r.top >= 0 && r.bottom <= innerHeight && r.left >= 0 && r.right <= innerWidth;}})()"));
                await Capture(web, prefix + "-small-options");
                await Script(web, "document.getElementById('settings-back').click()");
                await Wait(async () => await Text(web, "document.body.dataset.settingsOverview") == "services");
                await Script(web, $"document.querySelector('[data-page-id={pageId}]').click()");
                await Ready(web, prefix);
                Assert.Equal("", await Text(web, "document.querySelector('[data-field=apiKey]').value"));
                Assert.Equal("true", await Script(web, $"document.querySelector('.{prefix}-advanced').open"));
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData("Speech", "sc")]
    public async Task Failed_read_keeps_retry_and_write_only_key_usable(string pageId, string prefix)
    {
        await StaRunner.RunAsync(async () =>
        {
            var owner = new FixtureOwner(pageId) { FailReads = true };
            using var host = Factory(owner).CreateView(pageId);
            var window = Window(host);
            try
            {
                window.Show();
                await Wait(() => Task.FromResult(host.State == WebSurfaceState.Ready));
                var web = Assert.Single(host.Children.OfType<WebView2>());
                await Wait(async () => await Text(web, $"document.querySelector('.{prefix}-page')?.dataset.state ?? ''") == "read-error");
                Assert.Equal("Reload", await Text(web, "document.querySelector('[data-retry]').textContent"));
                Assert.Equal("false", await Script(web, "document.querySelector('[data-field=apiKey]').disabled"));
                Assert.Equal("true", await Script(web, "document.querySelector('[data-save]').disabled"));
                await Capture(web, prefix + "-read-error");
                owner.FailReads = false;
                await Script(web, "document.querySelector('[data-retry]').click()");
                await Ready(web, prefix);
            }
            finally { window.Close(); }
        });
    }

    private static SettingsWebRootFactory Factory(FixtureOwner owner) => new(
        new SettingsPageCatalog([new SettingsPageViewFactory(owner.SettingsPageId,
            _ => throw new InvalidOperationException(), "Synthetic service", order: 10)]), [owner],
        Path.Combine(Path.GetTempPath(), "margin-service-fixture-" + Guid.NewGuid().ToString("N")));
    private static Window Window(WebView2SurfaceHost host) => new()
        { Content = host, Width = 824, Height = 740, ShowInTaskbar = false, Left = -10000, Top = -10000 };
    private static Task Ready(WebView2 web, string prefix) => Wait(async () =>
        await Text(web, $"document.querySelector('.{prefix}-page')?.dataset.state ?? ''") == "ready");
    private static Task ReadyModel(WebView2 web) => Wait(async () => await Script(web,
        "!!document.querySelector('.connection-page') && document.querySelector('.settings-native-entry').getAttribute('aria-busy')==='false' && !document.querySelector('.settings-primary-button').disabled") == "true");
    private static Task<string> Script(WebView2 web, string script) => web.CoreWebView2.ExecuteScriptAsync(script);
    private static async Task<string> Text(WebView2 web, string script) =>
        JsonSerializer.Deserialize<string>(await Script(web, script)) ?? "";
    private static async Task Wait(Func<Task<bool>> condition, WebView2? diagnosticWeb = null, string stage = "")
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!await condition() && DateTime.UtcNow < deadline) await Task.Delay(25);
        var settled = await condition();
        var diagnostic = "";
        if (!settled && diagnosticWeb is not null)
            diagnostic = await Script(diagnosticWeb, "JSON.stringify({stage:'provider-menu-open',at:performance.now(),width:innerWidth,height:innerHeight,scrollY,menu:document.querySelectorAll('.margin-select-menu').length,traces:window.__serviceTiming||[]})");
        Assert.True(settled, "The offline service page did not settle. " + stage + " " + diagnostic);
    }
    private static Task<string> InstallTimingTrace(WebView2 web, string prefix) => Script(web, """
        (() => {
          const events=window.__serviceTiming=[];
          const select=document.querySelector('select[data-field]');
          function record(stage) {
            if(events.length>=32)events.shift();
            events.push({at:performance.now(),stage,width:innerWidth,height:innerHeight,scrollY,
              disabled:select?.disabled,triggerDisabled:select?.nextElementSibling?.disabled,
              options:select?.options.length,menu:document.querySelectorAll('.margin-select-menu').length});
          }
          window.addEventListener('resize',()=>record('resize'));
          document.addEventListener('scroll',()=>record('scroll'),true);
          if(select)new MutationObserver(()=>record('select-mutation')).observe(select,{attributes:true,childList:true,subtree:true});
          new MutationObserver(changes=>{if(changes.some(c=>[...c.addedNodes,...c.removedNodes].some(n=>n.classList?.contains('margin-select-menu'))))record('menu-mutation');})
            .observe(document.body,{childList:true});
          record('trace-installed');return true;
        })()
        """);
    private static async Task Capture(WebView2 web, string scene)
    {
        if (Environment.GetEnvironmentVariable("FGO_PET_MARGIN_SERVICE_CAPTURE_DIR") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory);
        await Task.Delay(300);
        using var file = File.Create(Path.Combine(directory, scene + "-webview.png"));
        await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, file);
        File.AppendAllText(Path.Combine(directory, "service-verification.jsonl"),
            JsonSerializer.Serialize(new { time = DateTimeOffset.UtcNow, request = "offline-service",
                stage = scene, result = "captured", errorCode = (string?)null }) + Environment.NewLine);
    }

    private sealed class FixtureOwner : ISettingsWebPage
    {
        private readonly Dictionary<string, object?> _snapshot;
        public FixtureOwner(string id)
        {
            SettingsPageId = id;
            _snapshot = id == "Speech" ? SpeechSnapshot() : ModelSnapshot();
        }
        public string SettingsPageId { get; }
        public string CommandPrefix => SettingsPageId == "Speech" ? "speech" : "modelConnection";
        public string ModulePath => SettingsPageId == "Speech" ? "pages/speech.js" : "pages/model-connection.js";
        public IReadOnlyList<string> Commands => SettingsPageId == "Speech"
            ? ["speech.get", "speech.setDraft", "speech.selectVoice", "speech.save", "speech.preview", "speech.stopPreview", "speech.clearKey", "speech.importVoice", "speech.deleteVoice"]
            : ["modelConnection.get", "modelConnection.setDraft", "modelConnection.setShowReasoning", "modelConnection.test", "modelConnection.refreshModels", "modelConnection.save", "modelConnection.clearKey"];
        public List<string> Calls { get; } = [];
        public bool FailReads { get; set; }
        public void SetFeedback(string status, string error = "")
        {
            _snapshot["statusText"] = status;
            _snapshot["errorText"] = error;
        }
        public ValueTask<WebSurfaceCommandResult> HandleCommandAsync(WebSurfaceMessage message, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Calls.Add(message.Type);
            if (FailReads && message.Type.EndsWith(".get", StringComparison.Ordinal))
                return ValueTask.FromResult(new WebSurfaceCommandResult(false, ErrorCode: "SETTINGS_UNAVAILABLE"));
            if (message.Type.EndsWith(".setDraft", StringComparison.Ordinal)
                && message.Payload.TryGetProperty("field", out var field)
                && message.Payload.TryGetProperty("value", out var value) && field.GetString() is { } name && name != "apiKey")
            {
                _snapshot[name] = value.ValueKind switch
                {
                    JsonValueKind.True => true, JsonValueKind.False => false,
                    JsonValueKind.Number => value.GetDouble(), _ => value.GetString(),
                };
                if (name == "provider")
                {
                    _snapshot["isIndexTts"] = value.GetString() == "indextts";
                    _snapshot["isOpenAiCompatible"] = value.GetString() == "openai";
                }
            }
            return ValueTask.FromResult(new WebSurfaceCommandResult(true, _snapshot));
        }
        private static Dictionary<string, object?> ModelSnapshot() => new()
        {
            ["providerId"] = "openai", ["providers"] = new[] { new { id = "openai", displayName = "OpenAI", defaultBaseUrl = "https://example.invalid/v1" }, new { id = "local", displayName = "Local provider", defaultBaseUrl = "http://localhost:1234/v1" } },
            ["baseUrl"] = "https://example.invalid/v1", ["modelId"] = "fixture-model",
            ["availableModels"] = new[] { new { id = "fixture-model", displayName = "Fixture model" } },
            ["contextWindowOverrideText"] = "", ["maxOutputTokensText"] = "2048", ["contextLimitText"] = "", ["providerStatusText"] = "Not tested", ["modelStatusText"] = "",
            ["keyStateText"] = "No key saved", ["isKeySaved"] = false, ["showReasoning"] = false, ["isBusy"] = false, ["statusText"] = "", ["errorText"] = "",
        };
        private static Dictionary<string, object?> SpeechSnapshot() => new()
        {
            ["enabled"] = false, ["providers"] = new[] { new { id = "openai", displayName = "OpenAI-compatible" }, new { id = "indextts", displayName = "IndexTTS" } },
            ["provider"] = "openai", ["isOpenAiCompatible"] = true, ["isIndexTts"] = false,
            ["openAiBaseUrl"] = "https://example.invalid/v1", ["openAiModel"] = "fixture-voice-model", ["openAiVoice"] = "sample",
            ["keyStateText"] = "No key saved", ["isKeySaved"] = false, ["indexTtsBaseUrl"] = "http://localhost:7860",
            ["voices"] = new[] { new { id = "voice-fixture", name = "Sample voice" } }, ["selectedVoiceId"] = "voice-fixture", ["voiceName"] = "",
            ["voiceCount"] = 1, ["voiceLimit"] = 10, ["autoReadEnabled"] = false, ["doNotDisturb"] = false, ["autoReadLimit"] = 300,
            ["rate"] = 1.0, ["volume"] = 0.8, ["isBusy"] = false, ["statusText"] = "", ["errorText"] = "",
        };
    }
}
