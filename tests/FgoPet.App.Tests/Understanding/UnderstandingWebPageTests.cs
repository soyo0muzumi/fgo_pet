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
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fgo-understanding-page-" + Guid.NewGuid().ToString("N"));
    private readonly UnderstandingService _owner;
    private readonly Lifetime _lifetime = new();
    private readonly Timeline _timeline = new();
    private ConversationScope? _scope = new("mash", "project");
    private readonly UnderstandingWebPage _page;
    public UnderstandingWebPageTests()
    {
        var db = new RuntimeDatabase(Path.Combine(_root, "runtime.db"), pooling: false); new RuntimeDatabaseMigrator(db).Migrate();
        _owner = new(db, new Sources()); _page = new(_owner, () => _scope, _lifetime, _timeline);
    }
    private ValueTask<WebSurfaceCommandResult> Send(string type, object payload, CancellationToken token = default) =>
        _page.HandleCommandAsync(new(type, "request", JsonSerializer.SerializeToElement(payload)), token);
    private ValueTask<WebSurfaceCommandResult> Get() => Send("understanding.get", new { pageId = "Understanding" });
    private object Configure(long revision = 0) => new { pageId = "Understanding", revision,
        policy = new { mode = "proposals", enabled = true, readSources = true, externalProcessing = false, saveCandidates = true, useInDialogue = true } };

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
        Assert.True(authority.IsActive(new("active", "mash", "p")));
        Assert.False(authority.IsActive(new("active", "other", "p"))); Assert.False(authority.IsActive(new("active", "mash", "other")));
        Assert.False(authority.IsActive(new("previous", "mash", "p")));
        repo.WriteState("LastActiveConversationId:mash", "previous", now); Assert.False(authority.IsActive(new("active", "mash", "p")));
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
        public long Generation => 0;
        public int Suspensions;
        public Action? OnSuspend;
        public Task InvalidateAsync(CancellationToken token) => Task.CompletedTask;
        public Task<IDisposable> SuspendAsync(CancellationToken token)
        { Suspensions++; OnSuspend?.Invoke(); return Task.FromResult<IDisposable>(new Lease()); }
        private sealed class Lease : IDisposable { public void Dispose() { } }
    }
}
