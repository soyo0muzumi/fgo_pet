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
            ConversationScope? pageScope = scope;
            var lifetime = new Lifetime(); var page = new UnderstandingWebPage(owner, () => pageScope, lifetime, timeline,
                _ => new("Synthetic companion", "Synthetic project", "Offline synthetic model · no dispatch"));
            var catalog = new SettingsPageCatalog([new SettingsPageViewFactory("Understanding",
                _ => throw new InvalidOperationException("SETTINGS_USE_WEB_ROOT"), "持续理解", group: "能力", order: 145)]);
            var vm = new SettingsViewModel(); vm.Navigate("Understanding");
            using var window = new SettingsWindow(vm, new SettingsWebRootFactory(catalog, [page], root));
            window.ShowInTaskbar = false; window.Left = window.Top = -10000; window.Show();
            var host = Assert.IsType<WebView2SurfaceHost>(window.Content);
            await WaitUntil(() => Task.FromResult(host.State == WebSurfaceState.Ready));
            var web = Assert.IsType<WebView2>(host.Children[0]);
            async Task<bool> Js(string code) => await web.CoreWebView2.ExecuteScriptAsync(code) == "true";
            await WaitUntil(() => Js("!!document.querySelector('[data-readiness]')?.textContent.includes('not available yet')"));
            Assert.Equal(new UnderstandingPolicy(), owner.State(scope).Policy);
            Assert.True(await Js("!document.querySelector('.understanding-page input[type=checkbox],.understanding-page select')"));
            await Capture(web, "01-default-off");
            Assert.True(await Js("document.querySelectorAll('[data-policy-actions] button').length===0"));
            // Arrange existing bounded permissions only in this temporary, synthetic owner.
            owner.Configure(scope, owner.State(scope).Revision, new(UnderstandingMode.Bounded, true, true, false, true, true), 71);
            ConversationSourceSnapshot Source(string id) => new(new("fixture-conversation", "fixture-role", "fixture-project"), id,
                new('A', 64), DateTimeOffset.UtcNow, ConversationSourceKind.User, "Synthetic fixture", 72);
            var visible = Source("visible-root"); var pending = Source("pending-root");
            owner.Apply(owner.Begin(scope, "fixture-batch", [visible, pending], coverage: [new(visible, 0, 4, 4), new(pending, 0, 4, 4)]),
                [new("visible", UnderstandingDomain.Self, UnderstandingKind.CurrentInterest, "assistant", "I am interested in helping you build a steady study routine, one small step at a time.", "During study planning", [visible]),
                 new("pending", UnderstandingDomain.Relationship, UnderstandingKind.Interpretation, "user", "synthetic-pending-marker " + string.Concat(Enumerable.Repeat("Planning can feel easier when choices are presented one at a time. This is a long synthetic interpretation used only to test wrapping and scrolling. ", 8)), "When deciding what to do next", [pending])]);
            await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('[data-refresh]').click()");
            await WaitUntil(() => Js("!!document.querySelector('[data-entries]')?.textContent.includes('I am interested in helping you build a steady study routine, one small step at a time.')"));
            Assert.True(await Js("!document.querySelector('[data-entries]').textContent.includes('synthetic-pending-marker')"));
            Assert.Equal("visible", Assert.Single(owner.Query(scope, "").Items).Id);
            await Capture(web, "02-accumulated");
            await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('[data-view=pending]').click()");
            await WaitUntil(() => Js("document.querySelector('[data-entries]').textContent.includes('synthetic-pending-marker')"));
            Assert.True(await Js("!document.querySelector('[data-entries]').textContent.includes('I am interested in helping you build a steady study routine, one small step at a time.')"));
            await Capture(web, "03-pending");
            var beforeLocalReview = owner.State(scope);
            owner.Configure(scope, beforeLocalReview.Revision, beforeLocalReview.Policy with { ReadSources = false });
            await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('[data-refresh]').click()");
            await WaitUntil(() => Js("!document.querySelector('.understanding-page').hasAttribute('aria-busy')"));
            await web.CoreWebView2.ExecuteScriptAsync("[...document.querySelectorAll('[data-entries] button')].find(b=>b.textContent==='Correct').click()");
            Assert.False(owner.State(scope).Policy.ReadSources);
            Assert.True(await Js("!!document.querySelector('[data-local-review]') && !document.querySelector('[data-entries] textarea')"));
            await web.CoreWebView2.ExecuteScriptAsync("[...document.querySelectorAll('[data-local-review] button')].find(b=>b.textContent==='Cancel').click()");
            Assert.False(owner.State(scope).Policy.ReadSources);
            await web.CoreWebView2.ExecuteScriptAsync("[...document.querySelectorAll('[data-entries] button')].find(b=>b.textContent==='Correct').click();[...document.querySelectorAll('[data-local-review] button')].find(b=>b.textContent==='Allow local review').click()");
            await WaitUntil(() => Task.FromResult(owner.State(scope).Policy.ReadSources));
            Assert.Equal(beforeLocalReview.Policy, owner.State(scope).Policy);
            Assert.Empty(owner.Query(scope, "").Items); // A read grant cannot revive the bounded item.
            var dormantVisible = owner.List(scope, false).Single(e => e.Id == "visible");
            owner.Review(scope, "fixture-existing-approval", dormantVisible.Id, dormantVisible.Revision, UnderstandingAction.Approve);
            await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('[data-refresh]').click()");
            await WaitUntil(() => Js("!document.querySelector('.understanding-page').hasAttribute('aria-busy')"));
            await web.CoreWebView2.ExecuteScriptAsync("[...document.querySelectorAll('[data-entries] button')].find(b=>b.textContent==='Correct').click();const edit=document.querySelector('[data-entries] textarea');edit.value='Present one small next step first; offer alternatives only when asked.';edit.dispatchEvent(new Event('input'));");
            await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('[data-refresh]').click()");
            await WaitUntil(() => Js("document.querySelector('[data-entries] textarea')?.value==='Present one small next step first; offer alternatives only when asked.'"));
            await web.CoreWebView2.ExecuteScriptAsync("[...document.querySelectorAll('[data-entries] button')].find(b=>b.textContent==='Cancel editing').click()");
            Assert.True(await Js("document.activeElement?.textContent==='Correct' && !document.querySelector('[data-entries] textarea')"));
            await web.CoreWebView2.ExecuteScriptAsync("[...document.querySelectorAll('[data-entries] button')].find(b=>b.textContent==='Correct').click()");
            Assert.True(await Js("document.activeElement?.tagName==='TEXTAREA' && document.activeElement.value==='Present one small next step first; offer alternatives only when asked.'"));
            var previous = owner.List(scope, true).Single();
            owner.Review(scope, "fixture-version-change", previous.Id, previous.Revision, UnderstandingAction.Correct, "Another synthetic correction");
            await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('[data-refresh]').click()");
            await WaitUntil(() => Js("[...document.querySelectorAll('[data-entries] button')].find(b=>b.textContent==='Save correction')?.disabled===true"));
            Assert.True(await Js("document.querySelector('[data-entries] textarea').value==='Present one small next step first; offer alternatives only when asked.'"));
            pageScope = new("other-fixture-role", "fixture-project");
            await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('[data-refresh]').click()");
            await WaitUntil(() => Js("!!document.querySelector('.understanding-empty')"));
            Assert.Equal(new UnderstandingPolicy(), owner.State(pageScope).Policy);
            pageScope = scope;
            await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('[data-refresh]').click()");
            await WaitUntil(() => Js("document.querySelector('[data-entries] textarea')?.value==='Present one small next step first; offer alternatives only when asked.'"));
            Assert.True(await Js("[...document.querySelectorAll('[data-entries] button')].find(b=>b.textContent==='Save correction').disabled"));
            await web.CoreWebView2.ExecuteScriptAsync("[...document.querySelectorAll('[data-entries] button')].find(b=>b.textContent==='Use current version').click();const save=[...document.querySelectorAll('[data-entries] button')].find(b=>b.textContent==='Save correction');save.click();save.click()");
            await WaitUntil(() => Task.FromResult(owner.List(scope, true).Single().Correction == "Present one small next step first; offer alternatives only when asked."));
            Assert.Equal(UnderstandingReview.Unreviewed, owner.List(scope, true).Single().Review);
            Assert.Single(owner.Query(scope, "").Items);
            await WaitUntil(() => Js("!document.querySelector('.understanding-page').hasAttribute('aria-busy')"));
            await Capture(web, "03b-corrected-still-pending");
            await web.CoreWebView2.ExecuteScriptAsync("[...document.querySelectorAll('[data-entries] button')].find(b=>b.textContent==='Approve this version').click()");
            await WaitUntil(() => Task.FromResult(owner.Query(scope, "").Items.Count == 2));
            Assert.Empty(owner.List(scope, true)); Assert.Equal(3, lifetime.Suspensions);
            await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('[data-view=accumulated]').click()");
            await WaitUntil(() => Js("document.querySelector('[data-entries]').textContent.includes('Present one small next step first; offer alternatives only when asked.')"));
            await web.CoreWebView2.ExecuteScriptAsync("[...document.querySelectorAll('[data-entries] button')].find(b=>b.textContent==='Forget').click();[...document.querySelectorAll('[data-forget] button')].find(b=>b.textContent==='Cancel').click()");
            Assert.True(await Js("document.activeElement?.textContent==='Forget' && !document.querySelector('[data-forget]')"));
            Assert.Equal(2, owner.List(scope, false).Count);
            await Capture(web, "04-approved");
            await web.CoreWebView2.ExecuteScriptAsync("[...document.querySelectorAll('[data-policy-actions] button')].find(b=>b.textContent==='Pause understanding').click();[...document.querySelectorAll('[data-policy-confirm] button')].find(b=>b.textContent==='Confirm').click()");
            await WaitUntil(() => Task.FromResult(!owner.State(scope).Policy.Enabled));
            var validationCount = timeline.Validations;
            Assert.Empty(owner.Query(scope, "").Items); Assert.Equal(validationCount, timeline.Validations);
            window.Close(); Assert.False(window.IsVisible); window.Show();
            await WaitUntil(() => Js("document.querySelector('[data-permission-note]').textContent.includes('paused')"));
            Assert.Same(host, window.Content); Assert.False(owner.State(scope).Policy.ExternalProcessing);
            await Capture(web, "05-revoked-reopened");
            await web.CoreWebView2.CallDevToolsProtocolMethodAsync("Emulation.setDeviceMetricsOverride",
                "{\"width\":320,\"height\":900,\"deviceScaleFactor\":1,\"mobile\":false}");
            Assert.True(await Js("document.documentElement.scrollWidth<=innerWidth"));
            await web.CoreWebView2.ExecuteScriptAsync("window.__keyTrace=[];for(const type of ['keydown','keypress','keyup','click'])document.addEventListener(type,e=>__keyTrace.push({type,key:e.key||'',code:e.code||'',prevented:e.defaultPrevented,tag:e.target.tagName,view:e.target.dataset?.view||''}),true)");
            await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('[data-view=accumulated]').focus()");
            await web.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", "{\"type\":\"keyDown\",\"key\":\"Tab\",\"code\":\"Tab\",\"windowsVirtualKeyCode\":9}");
            await web.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", "{\"type\":\"keyUp\",\"key\":\"Tab\",\"code\":\"Tab\",\"windowsVirtualKeyCode\":9}");
            Assert.True(await Js("document.activeElement===document.querySelector('[data-view=pending]')"));
            await web.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", "{\"type\":\"keyDown\",\"key\":\"Enter\",\"code\":\"Enter\",\"windowsVirtualKeyCode\":13,\"text\":\"\\r\",\"unmodifiedText\":\"\\r\"}");
            await web.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", "{\"type\":\"keyUp\",\"key\":\"Enter\",\"code\":\"Enter\",\"windowsVirtualKeyCode\":13}");
            var beforeControl = await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('[data-view=pending]').getAttribute('aria-pressed')");
            await web.CoreWebView2.ExecuteScriptAsync("window.__controlClicks=0;const b=document.createElement('button');b.id='synthetic-keyboard-control';b.textContent='Synthetic control';b.onclick=()=>__controlClicks++;document.body.append(b);b.focus()");
            await web.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", "{\"type\":\"keyDown\",\"key\":\"Enter\",\"code\":\"Enter\",\"windowsVirtualKeyCode\":13,\"text\":\"\\r\",\"unmodifiedText\":\"\\r\"}");
            await web.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", "{\"type\":\"keyUp\",\"key\":\"Enter\",\"code\":\"Enter\",\"windowsVirtualKeyCode\":13}");
            Console.WriteLine("UI_KEYBOARD_METADATA_ONLY tab=" + beforeControl + " controlClicks=" + await web.CoreWebView2.ExecuteScriptAsync("__controlClicks") + " trace=" + await web.CoreWebView2.ExecuteScriptAsync("__keyTrace"));
            Assert.True(await Js("__controlClicks===1"));
            Assert.True(await Js("document.querySelector('[data-view=pending]').getAttribute('aria-pressed')==='true'"));
            await web.CoreWebView2.ExecuteScriptAsync("document.getElementById('synthetic-keyboard-control').remove()");
            await Capture(web, "06-narrow-keyboard");
            pageScope = null;
            await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('[data-refresh]').click()");
            await WaitUntil(() => Js("!document.querySelector('[data-error]').hidden"));
            Assert.False(owner.State(scope).Policy.Enabled);
            await Capture(web, "07-unavailable");
            pageScope = scope;
            await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('[data-refresh]').click()");
            await WaitUntil(() => Js("document.querySelector('[data-error]').hidden"));
            window.Dispose(); Assert.Equal(WebSurfaceState.Closed, host.State);
            Assert.All(events, e => Assert.Matches("^[A-F0-9]{16}$", e.Correlation));
            var outputRoot = Environment.GetEnvironmentVariable("FGO_PET_H1_EVIDENCE_ROOT");
            if (!string.IsNullOrWhiteSpace(outputRoot))
                await File.WriteAllTextAsync(Path.Combine(outputRoot, "windows-owner-events.json"), JsonSerializer.Serialize(events));
            Console.WriteLine("H1_WINDOWS_METADATA_ONLY defaultOff=true pendingIsolated=true exactApproval=true reviewPaused=true hostReopened=true realProviderCalls=0");
        }, TimeSpan.FromSeconds(75));
    }

    private static async Task Capture(WebView2 web, string name)
    {
        var root = Environment.GetEnvironmentVariable("FGO_PET_H1_EVIDENCE_ROOT");
        if (string.IsNullOrWhiteSpace(root)) return;
        Directory.CreateDirectory(root);
        await web.CoreWebView2.ExecuteScriptAsync("for(let n=document.querySelector('.understanding-page');n;n=n.parentElement)n.scrollTop=0");
        await Task.Delay(60);
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
