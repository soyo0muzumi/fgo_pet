using System.Text.Json;
using System.IO;
using FgoPet.App.Bootstrap;
using FgoPet.App.Runtime;
using FgoPet.Core.Dialogue;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Understanding;
using FgoPet.Understanding.Contracts;
using FgoPet.Understanding.Desktop;
using FgoPet.UiSdk;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace FgoPet.App.Tests.Understanding;

public sealed class UnderstandingWebPageTests : IDisposable
{
    [Fact] public async Task Explicit_automation_command_binds_recipient_boundary_and_independent_dialogue_permission()
    {
        var settings = new AutomationSettings();
        var ledger = new DailyReviewLedger(Path.Combine(_root,"daily.db"),TimeProvider.System);
        var access = new DailyReviewBudgetAccess(ledger,settings,_owner);
        var page = new UnderstandingWebPage(_owner,()=>_scope,_lifetime,_timeline,automation:access);
        var get = await page.HandleCommandAsync(new("understanding.get","get",JsonSerializer.SerializeToElement(new{pageId="Understanding"})),default);
        var snapshot = JsonSerializer.SerializeToElement(get.Payload,new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var binding = snapshot.GetProperty("contextToken").GetString();
        var route = access.Status(_scope!).RouteHash;
        var policy = new {mode="bounded",enabled=true,readSources=true,externalProcessing=true,saveCandidates=true,useInDialogue=false};
        var payload = JsonSerializer.SerializeToElement(new{pageId="Understanding",contextToken=binding,revision=0,policy,dailyLimit=12,routeHash=route});
        Assert.Null(access.Get(_scope!));
        Assert.True((await page.HandleCommandAsync(new("understanding.automation","enable",payload),default)).Success);
        Assert.Equal(71,_owner.State(_scope!).AutoSince);
        Assert.Equal(FgoPet.Understanding.Contracts.UnderstandingMode.Bounded,_owner.State(_scope!).Policy.Mode);
        Assert.False(_owner.State(_scope!).Policy.UseInDialogue);
        Assert.NotNull(access.Get(_scope!));
        Assert.False((await page.HandleCommandAsync(new("understanding.automation","replay",payload),default)).Success);
        settings.Connection = new("deepseek","https://different.example/v1","deepseek-flash");
        Assert.Null(access.Get(_scope!));
        var refreshed = await page.HandleCommandAsync(new("understanding.get","refresh",JsonSerializer.SerializeToElement(new{pageId="Understanding"})),default);
        var current = JsonSerializer.SerializeToElement(refreshed.Payload,new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var changedPayload = JsonSerializer.SerializeToElement(new{pageId="Understanding",contextToken=current.GetProperty("contextToken").GetString(),
            revision=_owner.State(_scope!).Revision,policy,dailyLimit=12,routeHash=route});
        var suspensions=_lifetime.Suspensions;
        Assert.False((await page.HandleCommandAsync(new("understanding.automation","stale-route",changedPayload),default)).Success);
        Assert.Equal(suspensions,_lifetime.Suspensions);
    }
    private sealed class AutomationSettings:FgoPet.Dialogue.Settings.IDialogueSettingsStore
    {
        public FgoPet.Core.Settings.ModelConnectionSettings Connection = new("deepseek","https://synthetic.example/v1","deepseek-flash");
        public FgoPet.Dialogue.Settings.DialogueSettings Load()=>new(Connection,true);
        public void Save(FgoPet.Dialogue.Settings.DialogueSettings settings)=>throw new NotSupportedException();
    }
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fgo-understanding-page-" + Guid.NewGuid().ToString("N"));
    private readonly UnderstandingService _owner;
    private readonly Lifetime _lifetime = new();
    private readonly Timeline _timeline = new();
    private ConversationScope? _scope = new("mash", "project");
    private readonly UnderstandingWebPage _page;
    private string? _contextToken;
    public UnderstandingWebPageTests()
    {
        var db = new RuntimeDatabase(Path.Combine(_root, "runtime.db"), pooling: false); new RuntimeDatabaseMigrator(db).Migrate();
        _owner = new(db, new Sources()); _page = new(_owner, () => _scope, _lifetime, _timeline);
    }
    private async ValueTask<WebSurfaceCommandResult> Send(string type, object payload, CancellationToken token = default)
    {
        var fields = JsonSerializer.SerializeToElement(payload).EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
        if (type != "understanding.get" && !fields.ContainsKey("contextToken")) fields["contextToken"] = _contextToken;
        var result = await _page.HandleCommandAsync(new(type, "request", JsonSerializer.SerializeToElement(fields)), token);
        if (result.Success && result.Payload is not null)
        {
            var snapshot = JsonSerializer.SerializeToElement(result.Payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (snapshot.TryGetProperty("contextToken", out var context)) _contextToken = context.GetString();
        }
        return result;
    }
    private ValueTask<WebSurfaceCommandResult> Get() => Send("understanding.get", new { pageId = "Understanding" });
    private object Configure(long revision = 0) => new { pageId = "Understanding", revision,
        policy = new { mode = "proposals", enabled = true, readSources = true, externalProcessing = false, saveCandidates = true, useInDialogue = true } };

    [Fact] public async Task Snapshot_discloses_readable_context_and_usage_without_granting_permissions()
    {
        var before = _owner.State(_scope!);
        var result = await Get();
        var snapshot = JsonSerializer.SerializeToElement(result.Payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotEmpty(snapshot.GetProperty("contextToken").GetString()!);
        Assert.Equal("Current companion", snapshot.GetProperty("presentation").GetProperty("roleName").GetString());
        Assert.False(snapshot.GetProperty("presentation").GetProperty("automaticReviewAvailable").GetBoolean());
        Assert.Empty(snapshot.GetProperty("effectiveIds").EnumerateArray());
        Assert.Equal(before, _owner.State(_scope!));
    }

    [Fact] public async Task A_refresh_of_another_scope_cannot_rebind_an_old_editor_or_enable_its_policy()
    {
        await Get(); var oldToken = _contextToken;
        _scope = new("other", "project"); await Get();
        _scope = new("mash", "project"); await Get();
        var old = JsonSerializer.SerializeToElement(Configure()).EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
        old["contextToken"] = oldToken;
        Assert.Equal("UNDERSTANDING_SCOPE_CHANGED", (await Send("understanding.configure", old)).ErrorCode);
        Assert.Equal(new UnderstandingPolicy(), _owner.State(_scope!).Policy);
        Assert.Equal(0, _lifetime.Suspensions);
    }

    [Fact] public async Task Host_scope_and_exact_revision_control_configure_and_boundary_is_not_payload_authority()
    {
        Assert.True((await Get()).Success);
        Assert.True((await Send("understanding.configure", Configure())).Success);
        Assert.Equal(71, _owner.State(_scope!).AutoSince);
        Assert.False((await Send("understanding.configure", Configure())).Success);
        _scope = new("other", "project");
        Assert.Equal("UNDERSTANDING_SCOPE_CHANGED", (await Send("understanding.configure", Configure())).ErrorCode);
        Assert.Equal(UnderstandingMode.Off, _owner.State(_scope).Policy.Mode);
    }

    [Fact] public async Task Correct_keeps_a_candidate_pending_and_review_rejects_stale_scope_and_revision()
    {
        await Get(); Assert.True((await Send("understanding.configure", Configure())).Success);
        var source = new ConversationSourceSnapshot(new("fixture", "mash", "project"), "original", new('A', 64),
            DateTimeOffset.UtcNow, ConversationSourceKind.User, "Synthetic source", 72);
        _owner.Apply(_owner.Begin(_scope!, "fixture-batch", [source], coverage: [new(source, 0, 4, 4)]),
            [new("candidate", UnderstandingDomain.Relationship, UnderstandingKind.Interpretation, "user", "Synthetic interpretation", "Synthetic context", [source])]);
        await Get(); var entry = Assert.Single(_owner.List(_scope!, true)); var oldContext = _contextToken;
        var command = new { pageId = "Understanding", operationId = "fixture-correction", id = entry.Id, revision = entry.Revision,
            action = "correct", correction = "Synthetic correction" };
        Assert.True((await Send("understanding.review", command)).Success);
        var corrected = Assert.Single(_owner.List(_scope!, true));
        Assert.Equal(UnderstandingReview.Unreviewed, corrected.Review); Assert.Equal(UnderstandingEffect.Pending, corrected.Effect);
        Assert.Equal("Synthetic correction", corrected.Correction); Assert.Empty(_owner.Query(_scope!, "").Items);
        Assert.False((await Send("understanding.review", new { pageId = "Understanding", operationId = "different-operation",
            id = entry.Id, revision = entry.Revision, action = "approve" })).Success);
        _scope = new("other", "project"); await Get(); _scope = new("mash", "project"); await Get();
        var suspensions = _lifetime.Suspensions;
        Assert.Equal("UNDERSTANDING_SCOPE_CHANGED", (await Send("understanding.review", new { pageId = "Understanding",
            contextToken = oldContext, operationId = "stale-editor", id = entry.Id, revision = corrected.Revision,
            action = "correct", correction = "Must not apply" })).ErrorCode);
        Assert.Equal(suspensions, _lifetime.Suspensions);
        Assert.Equal(JsonSerializer.Serialize(corrected), JsonSerializer.Serialize(Assert.Single(_owner.List(_scope!, true))));
    }

    [Fact] public async Task Invalidated_host_generation_rejects_mutation_before_drain_and_refresh_rotates_binding()
    {
        await Get(); var before = _contextToken; _lifetime.Generation++;
        Assert.Equal("UNDERSTANDING_SCOPE_CHANGED", (await Send("understanding.configure", Configure())).ErrorCode);
        Assert.Equal(0, _lifetime.Suspensions); await Get(); Assert.NotEqual(before, _contextToken);
        Assert.Equal(new UnderstandingPolicy(), _owner.State(_scope!).Policy);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_observed_unavailable_or_different_scope_revokes_the_old_binding_even_after_return(bool unavailable)
    {
        await Get(); var previous = _contextToken;
        _scope = unavailable ? null : new("other", "project");
        Assert.False((unavailable ? await Get() : await Send("understanding.configure", Configure())).Success);
        _scope = new("mash", "project");
        Assert.Equal("UNDERSTANDING_SCOPE_CHANGED", (await Send("understanding.configure", Configure())).ErrorCode);
        Assert.Equal(0, _lifetime.Suspensions); Assert.Equal(new UnderstandingPolicy(), _owner.State(_scope).Policy);
        await Get(); Assert.NotEqual(previous, _contextToken);
    }
    [Fact] public async Task Invalid_payload_does_not_cancel_an_existing_dialogue()
    {
        await Get();
        var bad = await Send("understanding.configure", new { pageId = "Understanding", revision = 0, roleId = "other" });
        Assert.False(bad.Success); Assert.Equal(0, _lifetime.Suspensions);
    }
    [Theory]
    [InlineData("proposals")]
    [InlineData("bounded")]
    public async Task Staged_read_grant_uses_host_boundary_when_mode_was_already_selected(string mode)
    {
        Assert.True((await Get()).Success);
        object Policy(bool readSources) => new { mode, enabled = true, readSources, externalProcessing = true, saveCandidates = true, useInDialogue = true };
        Assert.True((await Send("understanding.configure", new { pageId = "Understanding", revision = 0, policy = Policy(false) })).Success);
        var beforeGrant = _owner.State(_scope!);
        Assert.Equal(0, beforeGrant.AutoSince);
        Assert.True((await Send("understanding.configure", new { pageId = "Understanding", revision = beforeGrant.Revision, policy = Policy(true) })).Success);
        Assert.Equal(71, _owner.State(_scope!).AutoSince);
        Assert.Equal(2, _lifetime.Suspensions);
    }
    [Fact] public async Task Production_review_entry_is_available_for_active_role_before_first_chat_without_enabling_grants()
    {
        var db = new RuntimeDatabase(Path.Combine(_root, "composition.db"), pooling: false); new RuntimeDatabaseMigrator(db).Migrate();
        var services = new ServiceCollection().AddFgoPet([], includeMemory: false, includeFocus: false, includeSpeech: false, includeAgentBackend: false);
        services.RemoveAll<RuntimeDatabase>(); services.AddSingleton(db);
        await using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<AppRuntime>().SetActiveRole(new("fixture", "default", "1.0.0", "mash"));
        var ownerPage = provider.GetServices<ISettingsWebPage>().Single(p => p.SettingsPageId == "Understanding").CreateSession();
        var result = await ownerPage.HandleCommandAsync(new("understanding.get", "read-only", JsonSerializer.SerializeToElement(new { pageId = "Understanding" })), default);
        Assert.True(result.Success, result.ErrorCode);
        Assert.Equal(new UnderstandingPolicy(), provider.GetRequiredService<UnderstandingService>().State(new("mash", null)).Policy);
        Assert.Empty(new FgoPet.Infrastructure.Dialogue.SqliteConversationRepository(db).LoadMessages("", "mash"));
    }
    [Fact] public async Task Dialogue_owned_scope_authority_rejects_other_role_project_and_previous_conversation()
    {
        var db = new RuntimeDatabase(Path.Combine(_root, "scope.db"), pooling: false); new RuntimeDatabaseMigrator(db).Migrate();
        var services = new ServiceCollection().AddFgoPet([], includeMemory: false, includeFocus: false, includeSpeech: false, includeAgentBackend: false);
        services.RemoveAll<RuntimeDatabase>(); services.AddSingleton(db);
        await using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<AppRuntime>().SetActiveRole(new("fixture", "default", "1.0.0", "mash"));
        var repo = provider.GetRequiredService<FgoPet.Infrastructure.Dialogue.SqliteConversationRepository>(); var now = DateTimeOffset.UtcNow;
        var context = new ContentContextKey("mash", "fixture", "1.0.0", "default", "p1", "k1");
        repo.CreateConversation("active", "mash", context, now, "p"); repo.CreateConversation("previous", "mash", context, now, "p");
        repo.WriteState("LastActiveConversationId:mash", "active", now);
        var authority = provider.GetRequiredService<IConversationScopeAuthority>();
        Assert.Equal(new ToolScope("active", "mash", "p"), authority.ActiveConversation);
        Assert.True(authority.IsActive(new("active", "mash", "p")));
        Assert.False(authority.IsActive(new("active", "other", "p"))); Assert.False(authority.IsActive(new("active", "mash", "other")));
        Assert.False(authority.IsActive(new("previous", "mash", "p")));
        repo.WriteState("LastActiveConversationId:mash", "previous", now); Assert.False(authority.IsActive(new("active", "mash", "p")));
        Assert.Equal(new ToolScope("previous", "mash", "p"), authority.ActiveConversation);
        repo.DeleteState("LastActiveConversationId:mash");
        Assert.Null(authority.ActiveConversation);
    }
    [Fact] public async Task Cancellation_after_suspension_or_scope_switch_cannot_commit_new_permissions()
    {
        await Get(); using var cts = new CancellationTokenSource(); _lifetime.OnSuspend = cts.Cancel;
        await Assert.ThrowsAsync<OperationCanceledException>(() => Send("understanding.configure", Configure(), cts.Token).AsTask());
        Assert.Equal(UnderstandingMode.Off, _owner.State(_scope!).Policy.Mode);
        _lifetime.OnSuspend = () => _scope = new("other", "project");
        Assert.Equal("UNDERSTANDING_SCOPE_CHANGED", (await Send("understanding.configure", Configure())).ErrorCode);
        Assert.Equal(UnderstandingMode.Off, _owner.State(new("mash", "project")).Policy.Mode);
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private sealed class Sources : IConversationSourceReader
    {
        public ConversationSourceSnapshot Read(ToolScope scope, string messageId, ConversationSourceKind kind) => throw new NotSupportedException();
        public bool IsCurrent(ConversationSourceSnapshot source) => true;
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
    private sealed class Lifetime : IDialogueContextLifetime
    {
        public long Generation { get; set; }
        public int Suspensions;
        public Action? OnSuspend;
        public Task InvalidateAsync(CancellationToken token) => Task.CompletedTask;
        public Task<IDisposable> SuspendAsync(CancellationToken token)
        { Suspensions++; OnSuspend?.Invoke(); return Task.FromResult<IDisposable>(new Lease()); }
        private sealed class Lease : IDisposable { public void Dispose() { } }
    }
}
