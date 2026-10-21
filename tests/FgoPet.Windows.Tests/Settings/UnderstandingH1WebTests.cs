using System.IO;
using System.Text.Json;
using FgoPet.App.Settings;
using FgoPet.Core.Dialogue;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Understanding;
using FgoPet.Understanding.Contracts;
using FgoPet.Understanding.Desktop;
using FgoPet.UiSdk;
using Microsoft.Web.WebView2.Wpf;
using Xunit;

namespace FgoPet.Windows.Tests.Settings;

public sealed class UnderstandingH1WebTests
{
    [Fact] public async Task Isolated_windows_settings_host_verifies_off_pending_approval_revocation_and_reopen()
    {
        await StaRunner.RunAsync(async () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "fgopet-h1-web-" + Guid.NewGuid().ToString("N"));
            var database = new RuntimeDatabase(Path.Combine(root, "runtime.db"), pooling: false);
            new RuntimeDatabaseMigrator(database).Migrate();
            var timeline = new Timeline(); var scope = new ConversationScope("fixture-role", "fixture-project");
            var events = new List<UnderstandingDiagnostic>();
            var owner = new UnderstandingService(database, timeline, routing: new FixtureRouting(), diagnostics: events.Add);
            var lifetime = new Lifetime(); var page = new UnderstandingWebPage(owner, () => scope, lifetime, timeline);
            var catalog = new SettingsPageCatalog([new SettingsPageViewFactory("Understanding",
                _ => throw new InvalidOperationException("SETTINGS_USE_WEB_ROOT"), "持续理解", group: "能力", order: 145)]);
            var vm = new SettingsViewModel(); vm.Navigate("Understanding");
            using var window = new SettingsWindow(vm, new SettingsWebRootFactory(catalog, [page], root));
            window.ShowInTaskbar = false; window.Left = window.Top = -10000; window.Show();
            var host = Assert.IsType<WebView2SurfaceHost>(window.Content);
            await WaitUntil(() => Task.FromResult(host.State == WebSurfaceState.Ready));
            var web = Assert.IsType<WebView2>(host.Children[0]);
            async Task<bool> Js(string code) => await web.CoreWebView2.ExecuteScriptAsync(code) == "true";
            await WaitUntil(() => Js("!!document.querySelector('[data-status]')?.textContent.includes('模式：off')"));
            Assert.Equal(new UnderstandingPolicy(), owner.State(scope).Policy);
            Assert.True(await Js("!['readSources','externalProcessing','saveCandidates','useInDialogue'].some(n=>document.querySelector('[data-policy]').elements[n].checked)"));
            await Capture(web, "01-default-off");
            await web.CoreWebView2.ExecuteScriptAsync("{const f=document.querySelector('[data-policy]');f.elements.mode.value='bounded';for(const n of ['readSources','saveCandidates','useInDialogue'])f.elements[n].checked=true;f.requestSubmit();}");
            await WaitUntil(() => Task.FromResult(owner.State(scope).Policy.ReadSources));
            Assert.False(owner.State(scope).Policy.ExternalProcessing); Assert.Equal(71, owner.State(scope).AutoSince);
            ConversationSourceSnapshot Source(string id) => new(new("fixture-conversation", "fixture-role", "fixture-project"), id,
                new('A', 64), DateTimeOffset.UtcNow, ConversationSourceKind.User, "Synthetic fixture", 72);
            var visible = Source("visible-root"); var pending = Source("pending-root");
            owner.Apply(owner.Begin(scope, "fixture-batch", [visible, pending], coverage: [new(visible, 0, 4, 4), new(pending, 0, 4, 4)]),
                [new("visible", UnderstandingDomain.Self, UnderstandingKind.CurrentInterest, "assistant", "synthetic-visible-marker", "only this fixture", [visible]),
                 new("pending", UnderstandingDomain.Relationship, UnderstandingKind.Interpretation, "user", "synthetic-pending-marker", "needs exact approval", [pending])]);
            await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('[data-refresh]').click()");
            await WaitUntil(() => Js("!!document.querySelector('[data-entries]')?.textContent.includes('synthetic-visible-marker')"));
            Assert.True(await Js("!document.querySelector('[data-entries]').textContent.includes('synthetic-pending-marker')"));
            Assert.Equal("visible", Assert.Single(owner.Query(scope, "").Items).Id);
            await Capture(web, "02-accumulated");
            await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('[data-view=pending]').click()");
            await WaitUntil(() => Js("document.querySelector('[data-entries]').textContent.includes('synthetic-pending-marker')"));
            Assert.True(await Js("!document.querySelector('[data-entries]').textContent.includes('synthetic-visible-marker')"));
            await Capture(web, "03-pending");
            await web.CoreWebView2.ExecuteScriptAsync("[...document.querySelectorAll('[data-entries] button')].find(b=>b.textContent==='批准此版本').click()");
            await WaitUntil(() => Task.FromResult(owner.Query(scope, "").Items.Count == 2));
            Assert.Empty(owner.List(scope, true)); Assert.Equal(2, lifetime.Suspensions);
            await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('[data-view=accumulated]').click()");
            await WaitUntil(() => Js("document.querySelector('[data-entries]').textContent.includes('synthetic-pending-marker')"));
            await Capture(web, "04-approved");
            await web.CoreWebView2.ExecuteScriptAsync("{const f=document.querySelector('[data-policy]');f.elements.readSources.checked=false;f.requestSubmit();}");
            await WaitUntil(() => Task.FromResult(!owner.State(scope).Policy.ReadSources));
            var validationCount = timeline.Validations;
            Assert.Empty(owner.Query(scope, "").Items); Assert.Equal(validationCount, timeline.Validations);
            window.Close(); Assert.False(window.IsVisible); window.Show();
            await WaitUntil(() => Js("!document.querySelector('[data-policy]').elements.readSources.checked"));
            Assert.Same(host, window.Content); Assert.False(owner.State(scope).Policy.ExternalProcessing);
            await Capture(web, "05-revoked-reopened");
            window.Dispose(); Assert.Equal(WebSurfaceState.Closed, host.State);
            Assert.All(events, e => Assert.Matches("^[A-F0-9]{16}$", e.Correlation));
            var outputRoot = Environment.GetEnvironmentVariable("FGO_PET_H1_EVIDENCE_ROOT");
            if (!string.IsNullOrWhiteSpace(outputRoot))
                await File.WriteAllTextAsync(Path.Combine(outputRoot, "windows-owner-events.json"), JsonSerializer.Serialize(events));
            Console.WriteLine("H1_WINDOWS_METADATA_ONLY defaultOff=true pendingIsolated=true exactApproval=true readRevoked=true hostReopened=true realProviderCalls=0");
        }, TimeSpan.FromSeconds(75));
    }

    private static async Task Capture(WebView2 web, string name)
    {
        var root = Environment.GetEnvironmentVariable("FGO_PET_H1_EVIDENCE_ROOT");
        if (string.IsNullOrWhiteSpace(root)) return;
        Directory.CreateDirectory(root);
        if (name is "02-accumulated" or "03-pending" or "04-approved")
        {
            await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('[data-entries]').scrollIntoView({block:'start'})");
            await Task.Delay(60);
        }
        using var stream = File.Create(Path.Combine(root, name + ".png"));
        await web.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, stream);
    }
    private static async Task WaitUntil(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(12);
        while (DateTime.UtcNow < deadline) { if (await condition()) return; await Task.Delay(30); }
        Assert.True(await condition(), "The isolated Understanding Windows page did not settle.");
    }
    private sealed class FixtureRouting : IUnderstandingRoutingPolicy
    { public bool IsLowImpact(UnderstandingProposal proposal, IReadOnlyList<ConversationSourceSnapshot> roots) => proposal.Id == "visible"; }
    private sealed class Lifetime : IDialogueContextLifetime
    {
        public long Generation => 0;
        public int Suspensions;
        public Task InvalidateAsync(CancellationToken token) => Task.CompletedTask;
        public Task<IDisposable> SuspendAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); Suspensions++; return Task.FromResult<IDisposable>(new Lease()); }
        private sealed class Lease : IDisposable { public void Dispose() { } }
    }
    private sealed class Timeline : IExperienceTimeline
    {
        public int Validations;
        public ConversationSourceSnapshot Read(ToolScope scope, string messageId, ConversationSourceKind kind) => throw new NotSupportedException();
        public bool IsCurrent(ConversationSourceSnapshot source) { Validations++; return true; }
        public long Boundary(ConversationScope scope) => 71;
        public ExperienceWindowPage ReadPage(ConversationScope scope, string conversationId, int startSequence = 1, ExperienceWindowCursor? cursor = null,
            int maxItems = 8, int maxScalars = 6000, CancellationToken token = default) => throw new NotSupportedException();
        public ExperienceWindowPage ReadAdjacent(ConversationScope scope, HistoryAnchor anchor, CancellationToken token = default) => throw new NotSupportedException();
    }
}
