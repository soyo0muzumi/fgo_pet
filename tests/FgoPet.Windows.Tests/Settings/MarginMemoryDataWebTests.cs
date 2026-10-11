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

/// <summary>Production pages with synthetic data. No actual export, restore, deletion or private memory.</summary>
public sealed class MarginMemoryDataWebTests
{
    [Theory]
    [InlineData("ConversationMemory", "cm")]
    [InlineData("Privacy", "pp")]
    public async Task Paper_pages_keep_confirmation_gates_and_narrow_actions_usable(string pageId, string prefix)
    {
        await StaRunner.RunAsync(async () =>
        {
            var owner = new FixtureOwner(pageId);
            using var host = Factory(owner).CreateView(pageId);
            var window = new Window { Content = host, Width = 824, Height = 740,
                ShowInTaskbar = false, Left = -10000, Top = -10000 };
            try
            {
                window.Show();
                await Wait(() => Task.FromResult(host.State == WebSurfaceState.Ready));
                var web = Assert.Single(host.Children.OfType<WebView2>());
                await Ready(web, prefix);
                Assert.Equal("Delete all user data", await Text(web, "document.querySelector('[data-delete-all]').textContent"));
                Assert.Equal("3px", await Text(web, "getComputedStyle(document.querySelector('[data-delete-all]')).borderRadius"));
                await Capture(web, prefix + "-normal");
                if (prefix == "cm")
                {
                    Assert.Equal("0", await Script(web, "document.querySelectorAll('[data-candidates] img').length"));
                    Assert.Contains("<img", await Text(web, "document.querySelector('[data-candidates]').textContent"));
                    await Click(web, "[data-approve-candidate]");
                    await Wait(() => Task.FromResult(owner.Calls.Contains("memory.approveCandidate")));
                    await Ready(web, prefix);
                }
                else
                {
                    await Click(web, "[data-export]");
                    await Wait(() => Task.FromResult(owner.Calls.Contains("privacy.export")));
                    await Ready(web, prefix);
                    await Click(web, "[data-create-backup]");
                    await Wait(() => Task.FromResult(owner.Calls.Contains("privacy.createBackup")));
                    await Ready(web, prefix);
                    await Click(web, "[data-restore-backup]");
                    Assert.DoesNotContain("privacy.restoreBackup", owner.Calls);
                    Assert.Equal("false", await Script(web, "document.querySelector('[data-restore-confirm]').hidden"));
                    await Capture(web, "pp-restore-confirm");
                    await Click(web, "[data-restore-cancel]");
                    Assert.DoesNotContain("privacy.restoreBackup", owner.Calls);
                    await Click(web, "[data-restore-backup]");
                    await Click(web, "[data-restore-confirm-btn]");
                    await Wait(() => Task.FromResult(owner.Calls.Contains("privacy.restoreBackup")));
                    await Ready(web, prefix);
                }
                await Click(web, "[data-delete-all]");
                Assert.DoesNotContain(owner.CommandPrefix + ".deleteAll", owner.Calls);
                Assert.Equal("false", await Script(web, "document.querySelector('[data-delete-confirm]').hidden"));
                await Capture(web, prefix + "-delete-confirm");
                await Click(web, "[data-delete-cancel]");
                Assert.DoesNotContain(owner.CommandPrefix + ".deleteAll", owner.Calls);
                window.Width = 280;
                await Wait(async () => await Script(web, "innerWidth <= 280") == "true");
                await Click(web, "[data-delete-all]");
                await Script(web, "document.querySelector('[data-delete-confirm-btn]').scrollIntoView({block:'center'})");
                Assert.Equal("true", await Script(web, "document.documentElement.scrollWidth <= innerWidth && document.querySelector('main').scrollWidth <= document.querySelector('main').clientWidth"));
                Assert.Equal("true", await Script(web, "(() => {const r=document.querySelector('[data-delete-confirm-btn]').getBoundingClientRect();return r.top>=0 && r.bottom<=innerHeight && r.left>=0 && r.right<=innerWidth;})()"));
                await Capture(web, prefix + "-small-confirm");
                await Click(web, "[data-delete-confirm-btn]");
                await Wait(() => Task.FromResult(owner.Calls.Contains(owner.CommandPrefix + ".deleteAll")));
                await Ready(web, prefix);
                await Click(web, "#settings-back");
                await Wait(async () => await Text(web, "document.body.dataset.settingsOverview") == "privacy");
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData("ConversationMemory", "cm")]
    [InlineData("Privacy", "pp")]
    public async Task Failed_read_shows_safe_English_error_without_raw_backend_details(string pageId, string prefix)
    {
        await StaRunner.RunAsync(async () =>
        {
            var owner = new FixtureOwner(pageId) { FailReads = true };
            using var host = Factory(owner).CreateView(pageId);
            var window = new Window { Content = host, Width = 824, Height = 740,
                ShowInTaskbar = false, Left = -10000, Top = -10000 };
            try
            {
                window.Show();
                await Wait(() => Task.FromResult(host.State == WebSurfaceState.Ready));
                var web = Assert.Single(host.Children.OfType<WebView2>());
                await Wait(async () => await Text(web, $"document.querySelector('.{prefix}-page')?.dataset.state ?? ''") == "error");
                Assert.Equal("This page is temporarily unavailable.", await Text(web, "document.querySelector('[data-error]').textContent"));
                Assert.Equal("false", await Script(web, "document.body.innerText.includes('SYNTHETIC_PRIVATE_ERROR')"));
                await Capture(web, prefix + "-read-error");
            }
            finally { window.Close(); }
        });
    }

    private static SettingsWebRootFactory Factory(FixtureOwner owner) => new(
        new SettingsPageCatalog([new SettingsPageViewFactory(owner.SettingsPageId,
            _ => throw new InvalidOperationException(), "Synthetic settings", order: 10)]), [owner],
        Path.Combine(Path.GetTempPath(), "margin-memory-data-" + Guid.NewGuid().ToString("N")));
    private static Task Click(WebView2 web, string selector) => Script(web, $"document.querySelector({JsonSerializer.Serialize(selector)}).click()");
    private static Task Ready(WebView2 web, string prefix) => Wait(async () =>
        await Text(web, $"document.querySelector('.{prefix}-page')?.dataset.state ?? ''") == "ready"
        && await Script(web, $"document.querySelector('.{prefix}-page').getAttribute('aria-busy') === 'false'") == "true");
    private static Task<string> Script(WebView2 web, string script) => web.CoreWebView2.ExecuteScriptAsync(script);
    private static async Task<string> Text(WebView2 web, string script) => JsonSerializer.Deserialize<string>(await Script(web, script)) ?? "";
    private static async Task Wait(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!await condition() && DateTime.UtcNow < deadline) await Task.Delay(25);
        Assert.True(await condition(), "The synthetic memory/data page did not settle.");
    }
    private static async Task Capture(WebView2 web, string scene)
    {
        if (Environment.GetEnvironmentVariable("FGO_PET_MARGIN_MEMORY_DATA_CAPTURE_DIR") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory);
        await Task.Delay(300);
        using var file = File.Create(Path.Combine(directory, scene + "-webview.png"));
        await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, file);
        File.AppendAllText(Path.Combine(directory, "memory-data-verification.jsonl"), JsonSerializer.Serialize(new
            { time = DateTimeOffset.UtcNow, request = "synthetic-memory-data", stage = scene, result = "captured", errorCode = (string?)null }) + Environment.NewLine);
    }

    private sealed class FixtureOwner(string pageId) : ISettingsWebPage
    {
        public string SettingsPageId => pageId;
        public string CommandPrefix => pageId == "Privacy" ? "privacy" : "memory";
        public string ModulePath => pageId == "Privacy" ? "pages/privacy.js" : "pages/conversation-memory.js";
        public IReadOnlyList<string> Commands => pageId == "Privacy"
            ? ["privacy.get", "privacy.export", "privacy.createBackup", "privacy.restoreBackup", "privacy.deleteAll"]
            : ["memory.get", "memory.approveCandidate", "memory.deleteAll"];
        public List<string> Calls { get; } = [];
        public bool FailReads { get; init; }
        public ValueTask<WebSurfaceCommandResult> HandleCommandAsync(WebSurfaceMessage message, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(message.Type); // Never record payloads or memory bodies.
            if (FailReads) throw new InvalidOperationException("SYNTHETIC_PRIVATE_ERROR");
            object snapshot = pageId == "Privacy" ? new
            {
                exportStatusText = "", backupStatusText = "", deleteStatusText = "",
                canExport = true, canCreateBackup = true, canRestoreBackup = true, isBusy = false
            } : (object)new
            {
                memoryEnabled = true,
                candidates = new[] { new { id = "candidate-fixture", text = "<img src=x onerror=alert(1)> Synthetic candidate", hasReplacement = false } },
                storedMemories = new[] { new { id = "memory-fixture", text = "Synthetic confirmed memory", isEnabled = true, version = 1 } },
                selectedCandidateId = "candidate-fixture", selectedMemoryId = "memory-fixture",
                candidateEditText = "Synthetic candidate", memoryEditText = "Synthetic confirmed memory",
                candidateDetails = "Synthetic source", memoryDetails = "Synthetic source",
                statusText = "", candidatesStatusText = "", storedMemoriesStatusText = "", isBusy = false
            };
            return ValueTask.FromResult(new WebSurfaceCommandResult(true, snapshot));
        }
    }
}
