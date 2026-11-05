using System.IO;
using System.Text.Json;
using FgoPet.App.Memory;
using FgoPet.App.Settings;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Memory;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.Infrastructure.Memory;
using FgoPet.Infrastructure.Persistence;
using FgoPet.UiSdk;
using FgoPet.Understanding;
using FgoPet.Understanding.Contracts;
using FgoPet.Understanding.Desktop;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Xunit;

namespace FgoPet.Windows.Tests.Settings;

/// <summary>Real SettingsWindow, bridge and both owners, using only a temporary synthetic database.</summary>
public sealed class RelationshipV2WebTests
{
    [Fact]
    public async Task Final_tabs_preserve_owner_isolation_drafts_and_responsive_layout()
    {
        await StaRunner.RunAsync(async () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "fgopet-relationship-v2-" + Guid.NewGuid().ToString("N"));
            var db = new RuntimeDatabase(Path.Combine(root, "runtime.db"), pooling: false);
            new RuntimeDatabaseMigrator(db).Migrate();
            var conversations = new SqliteConversationRepository(db);
            conversations.CreateConversation("fixture-conversation", "fixture-role",
                new ContentContextKey("fixture-role", "test", "1.0.0", "casual", "p1", "k1"), DateTimeOffset.UtcNow);
            var repository = new SqliteMemoryRepository(db);
            repository.AddCandidate(new("saved", "fixture-role", "fixture-conversation", "喜欢安静地读书。", DateTimeOffset.UtcNow));
            repository.AddCandidate(new("pending", "fixture-role", "fixture-conversation", "<img src=x onerror=alert(1)> 合成候选", DateTimeOffset.UtcNow));
            repository.AddCandidate(new("reject", "fixture-role", "fixture-conversation", "仅供拒绝路径使用的合成候选。", DateTimeOffset.UtcNow));
            var memory = new MemoryCandidateService(repository, TimeProvider.System);
            await memory.ReviewAsync("fixture-role", "saved", MemoryReviewAction.Approve, null, default);
            using var model = new MemoryViewModel(memory);
            model.SetActiveServant("fixture-role"); await model.RefreshAsync();
            var scope = new ConversationScope("fixture-role", "fixture-project");
            var timeline = new Timeline(); var lifetime = new Lifetime();
            var diagnostics = new List<UnderstandingDiagnostic>();
            var understanding = new UnderstandingService(db, timeline, diagnostics: diagnostics.Add);
            understanding.Configure(scope, 0, new(UnderstandingMode.Proposals, true, true, false, true, false), 71);
            var source = new ConversationSourceSnapshot(new("fixture-conversation", "fixture-role", "fixture-project"),
                "source", new('A', 64), DateTimeOffset.UtcNow, ConversationSourceKind.Assistant, "合成来源", 72);
            var receipt = understanding.Apply(understanding.Begin(scope, "batch", [source]),
                [new("thought", UnderstandingDomain.Self, UnderstandingKind.CurrentInterest, "assistant", "我最近在意的是，把复杂的事拆成容易开始的一小步。", "合成条件", [source]),
                 new("visible-thought", UnderstandingDomain.Self, UnderstandingKind.CurrentStance, "assistant", "比起一次安排得很满，我更愿意留一点余地，和你慢慢推进。", "合成情境", [source])]);
            Assert.All(receipt.Items, item => Assert.Null(item.Code));
            var visible = understanding.List(scope, true).Single(item => item.Id == "visible-thought");
            understanding.Review(scope, "fixture-approve", visible.Id, visible.Revision, UnderstandingAction.Approve);
            var access = new DailyReviewBudgetAccess(new DailyReviewLedger(Path.Combine(root, "daily.db"), TimeProvider.System), new AutomationSettings(), understanding);
            var pages = new ISettingsWebPage[] { new MemoryWebPage(model),
                new UnderstandingWebPage(understanding, () => scope, lifetime, timeline, _ => new("合成角色", "合成项目", "离线合成模型，无模型派发"), automation: access) };
            var catalog = new SettingsPageCatalog(pages.Select((page, i) => new SettingsPageViewFactory(page.SettingsPageId,
                _ => throw new InvalidOperationException("SETTINGS_USE_WEB_ROOT"), page.SettingsPageId, order: i + 1)));
            var vm = new SettingsViewModel(); vm.Navigate("ConversationMemory");
            using var window = new SettingsWindow(vm, new SettingsWebRootFactory(catalog, pages, root));
            window.ShowInTaskbar = false; window.Left = window.Top = -10000; window.Show();
            var host = Assert.IsType<WebView2SurfaceHost>(window.Content);
            await Wait(() => Task.FromResult(host.State == WebSurfaceState.Ready));
            var web = Assert.IsType<WebView2>(host.Children[0]);
            Task<string> Script(string code) => web.CoreWebView2.ExecuteScriptAsync(code);
            async Task<bool> Js(string code) => await Script(code) == "true";
            async Task Click(string selector)
            {
                Assert.True(await Js($$"""
                    (()=>{ const el=document.querySelector({{JsonSerializer.Serialize(selector)}}); if(!el||el.disabled)return false;
                    el.scrollIntoView({block:'center'});const r=el.getBoundingClientRect();if(!r.width||!r.height)return false;el.click();return true; })()
                    """));
            }
            Task MemoryReady() => Wait(() => Js("document.querySelector('.cm-page')?.dataset.state==='ready' && document.querySelector('.cm-page')?.getAttribute('aria-busy')==='false'"));
            Task ThoughtsReady() => Wait(() => Js("!!document.querySelector('[data-groups] button') && !document.querySelector('.understanding-page')?.hasAttribute('aria-busy')"));
            async Task Capture(string stage)
            {
                if (Environment.GetEnvironmentVariable("FGO_PET_RELATIONSHIP_EVIDENCE_ROOT") is not { Length: > 0 } output) return;
                Directory.CreateDirectory(output);
                await Script("document.querySelector('#settings-scroll').scrollTop=0");
                await Task.Delay(100);
                using var stream = File.Create(Path.Combine(output, stage + ".png"));
                await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
                File.AppendAllText(Path.Combine(output, "verification.jsonl"), JsonSerializer.Serialize(new
                    { time = DateTimeOffset.UtcNow, correlation = "synthetic-v2", stage, result = "captured", errorCode = (string?)null }) + Environment.NewLine);
            }
            await MemoryReady();
            Assert.True(await Js("document.querySelector('#page-title').textContent==='关系与成长' && document.querySelectorAll('[role=tab][aria-selected=true]').length===1"));
            Assert.True(await Js("document.querySelector('[data-memory-pane=candidates]').hidden && !document.querySelector('[data-memory-pane=confirmed]').hidden"));
            Assert.True(await Js("getComputedStyle(document.querySelector('[data-memory-pane=confirmed] h3')).fontSize==='13px'"));
            await Capture("memory-wide");
            await Click("[data-memory-view=candidates]");
            Assert.True(await Js("document.querySelectorAll('[data-candidates] img').length===0 && document.querySelector('[data-candidates]').textContent.includes('<img')"));
            await Click("[data-candidates] button"); await MemoryReady();
            await Capture("memory-candidate");
            await Click("[data-memory-view=confirmed]");
            await Click("[data-memories] .cm-list-button"); await MemoryReady();
            await Script("const edit=document.querySelector('[data-memory-edit]');edit.value='尚未提交的合成草稿';edit.dispatchEvent(new Event('input'))");
            var before = understanding.State(scope);
            var pendingRevision = Assert.Single(understanding.List(scope, true)).Revision;
            await Click("[data-relationship-page=Understanding]"); await ThoughtsReady();
            Assert.Equal(before, understanding.State(scope)); Assert.Equal(0, lifetime.Suspensions);
            await Capture("thoughts-wide");
            await Script("window.__sourceToggleBefore = document.querySelector('[data-detail-toggle]').getBoundingClientRect().toJSON();document.querySelector('[data-detail-toggle]').click()"); await Task.Delay(80);
            Assert.True(await Js("(() => {const after=document.querySelector('[data-detail-toggle]').getBoundingClientRect();return Math.abs(after.x-window.__sourceToggleBefore.x)<1 && Math.abs(after.y-window.__sourceToggleBefore.y)<1;})()"),
                await Script("JSON.stringify({before:window.__sourceToggleBefore,after:document.querySelector('[data-detail-toggle]').getBoundingClientRect().toJSON()})"));
            Assert.True(await Js("document.querySelectorAll('[data-previous-wording]').length===0"));
            var beforeArchive = understanding.State(scope);
            await Click("[data-archive]");
            Assert.True(await Js("document.querySelector('[data-archive]').getAttribute('aria-expanded')==='true' && !document.querySelector('[data-archived]').hidden && !!document.querySelector('[data-entries] .understanding-card')"));
            await Click("[data-archive]");
            Assert.True(await Js("document.querySelector('[data-archive]').getAttribute('aria-expanded')==='false' && document.querySelector('[data-archived]').hidden && document.querySelector('[data-view=accumulated]').hidden"));
            Assert.Equal(beforeArchive, understanding.State(scope)); Assert.Equal(0, lifetime.Suspensions);
            await Capture("thoughts-details");
            Assert.True(await Js("document.querySelector('[data-entries]').textContent.includes('依据 1 条对话引用')"));
            await Click("[data-pending-attention]"); await ThoughtsReady();
            await Capture("thoughts-pending");
            await Script("document.querySelector('[data-detail-toggle]').click()");
            await Task.Delay(100);
            await Click("[data-correct]");
            await Script("const edit=document.querySelector('.understanding-editor textarea');edit.value='合成纠正，仍待批准。';edit.dispatchEvent(new Event('input'))");
            await Capture("thoughts-correction");
            await Script("[...document.querySelectorAll('.understanding-editor button')].find(b=>b.textContent==='保存纠正').click()");
            await Wait(() => Task.FromResult(understanding.List(scope, true).Single().Revision > pendingRevision));
            Assert.Equal(UnderstandingReview.Unreviewed, Assert.Single(understanding.List(scope, true)).Review);
            Assert.Single(await memory.ListAllAsync("fixture-role", default));
            await ThoughtsReady();
            await Script("[...document.querySelectorAll('[data-entries] button')].find(b=>b.textContent==='批准此版本').click()");
            await Wait(() => Task.FromResult(understanding.List(scope, true).Count == 0));
            Assert.Empty(understanding.Query(scope, "").Items); // Approval does not grant reply use.
            await Click("[data-relationship-page=ConversationMemory]"); await MemoryReady();
            Assert.True(await Js("document.querySelector('[data-memory-edit]').value==='尚未提交的合成草稿'"));
            Assert.Equal("喜欢安静地读书。", Assert.Single(await memory.ListAllAsync("fixture-role", default)).Text);
            var stored = Assert.Single(await memory.ListAllAsync("fixture-role", default));
            await memory.ReviewMemoryAsync("fixture-role", stored.MemoryId, MemoryReviewAction.Edit, "后台更新后的合成内容。", default, stored.Version);
            await Click("[data-memory-settings]"); await Click("[data-refresh]"); await MemoryReady();
            await Click("[data-memories] .cm-list-button"); await MemoryReady();
            Assert.True(await Js("document.querySelector('[data-edit-memory]').disabled && !document.querySelector('[data-stale-draft]').hidden"));
            await Capture("memory-version-conflict");
            await Click("[data-rebase-draft]");
            await Click("[data-memory-settings]");
            await Click("[data-edit-memory]"); await MemoryReady();
            Assert.Equal("尚未提交的合成草稿", Assert.Single(await memory.ListAllAsync("fixture-role", default)).Text);
            Assert.True(await Js("document.querySelector('.navigation-draft')===null"));
            await Click("[data-memories] .cm-row-actions button:nth-child(2)"); await MemoryReady();
            Assert.False(Assert.Single(await memory.ListAllAsync("fixture-role", default)).IsEnabled);
            await Click("[data-memories] .cm-row-actions button:nth-child(3)"); await MemoryReady();
            Assert.Single(await memory.ListAllAsync("fixture-role", default));
            await Capture("memory-delete-confirm");
            await Click("[data-single-delete] button:last-child");
            foreach (var width in new[] { 360, 390, 720, 721, 850, 1280 })
            {
                await web.CoreWebView2.CallDevToolsProtocolMethodAsync("Emulation.setDeviceMetricsOverride",
                    JsonSerializer.Serialize(new { width, height = 900, deviceScaleFactor = 1, mobile = false }));
                Assert.True(await Js("document.documentElement.scrollWidth<=innerWidth && document.querySelector('#settings-scroll').scrollWidth<=document.querySelector('#settings-scroll').clientWidth"));
                await Capture("memory-viewport-" + width);
                await Click("[data-relationship-page=Understanding]"); await ThoughtsReady();
                await Script("document.querySelector('[data-view=accumulated]').click();document.querySelector('[data-groups] button').click()");
                Assert.True(await Js("document.querySelectorAll('.understanding-card').length>0 && document.querySelector('#settings-scroll').scrollWidth<=document.querySelector('#settings-scroll').clientWidth"));
                await Capture("thoughts-viewport-" + width);
                await Click("[data-open-settings]");
                Assert.True(await Js("document.querySelector('[data-settings]').getBoundingClientRect().right<=innerWidth && document.querySelector('[data-settings]').getBoundingClientRect().left>=0"));
                Assert.True(await Js("(() => { const footer=document.querySelector('.understanding-drawer-footer').getBoundingClientRect(), drawer=document.querySelector('[data-settings]').getBoundingClientRect(); return footer.height>0 && footer.top>=drawer.top && footer.bottom<=drawer.bottom; })()"));
                Assert.True(await Js("document.querySelectorAll('.understanding-presets input').length===3 && document.querySelector('.understanding-presets input:checked').value==='proposals'"));
                Assert.True(await Js("[...document.querySelectorAll('.understanding-drawer-footer button')].find(b=>b.textContent==='好的').disabled"));
                await Capture("settings-viewport-" + width);
                await Click("[data-close-settings]");
                await Click("[data-relationship-page=ConversationMemory]"); await MemoryReady();
            }
            await web.CoreWebView2.CallDevToolsProtocolMethodAsync("Emulation.clearDeviceMetricsOverride", "{}");
            window.Width = 1000;
            foreach (var zoom in new[] { 1.25, 1.5 })
            {
                web.ZoomFactor = zoom; await Task.Delay(100);
                Assert.True(await Js("document.documentElement.scrollWidth<=innerWidth && document.querySelector('#settings-scroll').scrollWidth<=document.querySelector('#settings-scroll').clientWidth"));
                await Capture("memory-zoom-" + zoom);
                await Click("[data-relationship-page=Understanding]"); await ThoughtsReady();
                await Capture("thoughts-zoom-" + zoom);
                await Click("[data-relationship-page=ConversationMemory]"); await MemoryReady();
            }
            web.ZoomFactor = 1;
            window.Width = window.MinWidth;
            await Capture("desktop-minimum-width");
            await Click("[data-memory-settings]"); await Capture("memory-settings");
            model.SetActiveServant("other-role"); await model.RefreshAsync();
            await Click("[data-refresh]"); await MemoryReady();
            Assert.True(await Js("document.querySelector('[data-memory-edit]').value==='' && !document.body.textContent.includes('尚未提交的合成草稿')"));
            model.SetActiveServant("fixture-role"); await model.RefreshAsync();
            await Click("[data-refresh]"); await MemoryReady();
            await Click("[data-memory-view=candidates]"); await MemoryReady();
            await Click("[data-approve-candidate]"); await MemoryReady();
            Assert.Equal(2, (await memory.ListAllAsync("fixture-role", default)).Count);
            Assert.Single((await memory.ListCandidatesAsync("fixture-role", default)).Where(item => item.Status == MemoryCandidateStatus.Pending));
            await Click("[data-candidates] button"); await MemoryReady();
            await Click("[data-reject-candidate]"); await MemoryReady();
            Assert.Empty((await memory.ListCandidatesAsync("fixture-role", default)).Where(item => item.Status == MemoryCandidateStatus.Pending));
            Assert.Equal(2, understanding.List(scope, false).Count);
            await Click("[data-memory-view=confirmed]");
            await Click("[data-memories] .cm-row-actions button:nth-child(3)"); await MemoryReady();
            await Click("[data-single-delete] button:first-of-type"); await MemoryReady();
            Assert.Single(await memory.ListAllAsync("fixture-role", default));
            Assert.Equal(2, understanding.List(scope, false).Count);
            Assert.All(diagnostics, entry => Assert.Matches("^[A-F0-9]{16}$", entry.Correlation));
            Assert.Equal(2, lifetime.Suspensions); // Only explicit correction and approval.
            // The three UI operations must map to different existing owner lifecycle commands.
            var policy = understanding.State(scope);
            understanding.Configure(scope, policy.Revision, policy.Policy with { UseInDialogue = true });
            Assert.Equal(2, understanding.Query(scope, "").Items.Count);
            await Click("[data-relationship-page=Understanding]"); await ThoughtsReady();
            await Script("document.querySelector('[data-view=accumulated]').click();document.querySelector('[data-groups] button').click()");
            if (await Js("document.querySelector('[data-entry-id=visible-thought] .understanding-item-details').hidden"))
                await Click("[data-entry-id=visible-thought] [data-detail-toggle]");
            await Click("[data-entry-id=visible-thought] [data-suspend-thought]"); await ThoughtsReady();
            Assert.Equal(UnderstandingLife.Dormant, understanding.List(scope, false).Single(item => item.Id == "visible-thought").Life);
            Assert.Single(understanding.Query(scope, "").Items);
            await Click("[data-archive]");
            Assert.True(await Js("!!document.querySelector('[data-entries] [data-entry-id=thought]') && !!document.querySelector('[data-archived] [data-entry-id=visible-thought]')"));
            await Capture("thoughts-archived-inline");
            await Click("[data-archived] [data-restore-thought]"); await ThoughtsReady();
            Assert.Equal(UnderstandingLife.Active, understanding.List(scope, false).Single(item => item.Id == "visible-thought").Life);
            Assert.Equal(2, understanding.Query(scope, "").Items.Count);
            await Click("[data-entry-id=visible-thought] [data-resolve-thought]"); await ThoughtsReady();
            Assert.Equal(UnderstandingLife.Resolved, understanding.List(scope, false).Single(item => item.Id == "visible-thought").Life);
            Assert.Single(understanding.Query(scope, "").Items);
            await Click("[data-archived] [data-delete-thought]");
            Assert.Equal(2, understanding.List(scope, false).Count); // Opening confirmation does not delete.
            await Click("[data-forget] .understanding-actions button:first-child"); await ThoughtsReady();
            Assert.Single(understanding.List(scope, false)); Assert.Empty(understanding.Changes(scope, "visible-thought"));
            Assert.Single(await memory.ListAllAsync("fixture-role", default));
            Assert.Equal(6, lifetime.Suspensions);
            Console.WriteLine("RELATIONSHIP_V2_METADATA_ONLY realOwners=true syntheticDatabase=true modelDispatches=0 widths=360,390,720,721,850,1280");
        }, TimeSpan.FromSeconds(120));
    }

    private static async Task Wait(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline) { if (await condition()) return; await Task.Delay(30); }
        Assert.True(await condition(), "The isolated relationship page did not settle.");
    }
    private sealed class AutomationSettings : FgoPet.Dialogue.Settings.IDialogueSettingsStore
    {
        public FgoPet.Dialogue.Settings.DialogueSettings Load() => new(new FgoPet.Core.Settings.ModelConnectionSettings("deepseek", "https://synthetic.example/v1", "synthetic-model"), true);
        public void Save(FgoPet.Dialogue.Settings.DialogueSettings settings) => throw new NotSupportedException();
    }
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
        public ConversationSourceSnapshot Read(ToolScope scope, string messageId, ConversationSourceKind kind) => throw new NotSupportedException();
        public bool IsCurrent(ConversationSourceSnapshot source) => true;
        public long Boundary(ConversationScope scope) => 71;
        public ExperienceWindowPage ReadPage(ConversationScope scope, string conversationId, int startSequence = 1, ExperienceWindowCursor? cursor = null,
            int maxItems = 8, int maxScalars = 6000, CancellationToken token = default) => throw new NotSupportedException();
        public ExperienceWindowPage ReadAdjacent(ConversationScope scope, HistoryAnchor anchor, CancellationToken token = default) => throw new NotSupportedException();
    }
}
