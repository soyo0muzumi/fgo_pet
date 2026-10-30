using FgoPet.App.Dialogue;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Packs;
using FgoPet.Kernel.Conversation;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

public sealed class ExpressionCandidateInboxTests
{
    private static RuntimeDatabase Database()
    {
        var db = new RuntimeDatabase(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "expression-inbox-" + Guid.NewGuid().ToString("N"), "runtime.db"), pooling: false);
        new RuntimeDatabaseMigrator(db).Migrate();
        return db;
    }
    private static ConversationSourceSnapshot Source(string message = "u1", string conversation = "c1",
        ConversationSourceKind kind = ConversationSourceKind.User, string project = "p1") =>
        new(new(conversation, "mash_kyrielight", project), message, new string('A', 64), DateTimeOffset.UtcNow, kind, null);

    [Fact]
    public void Passive_user_preference_is_persisted_deduplicated_and_never_activated()
    {
        var db = Database(); var inbox = new ExpressionCandidateInbox(db);
        Assert.Equal("collected", inbox.Collect(Source(), "可以说短一点，不用每次总结"));
        Assert.Equal("duplicate_source", inbox.Collect(Source(), "可以说短一点，不用每次总结"));
        Assert.Equal("deduplicated", inbox.Collect(Source("u2"), "说短一点"));
        var rows = new ExpressionCandidateInbox(db).ReadInbox(Source().Scope);
        var row = Assert.Single(rows);
        Assert.Equal("pending", row.ReviewStatus);
        Assert.Equal("user_requested_brevity", row.Situation);
        Assert.Equal(2, row.Source.Count);
        Assert.InRange(row.Confidence, 0, 1);
        Assert.DoesNotContain("可以说短一点", inbox.Export().GetRawText());
        Assert.DoesNotContain("active", inbox.Export().GetRawText());
    }

    [Theory]
    [InlineData(ConversationSourceKind.Assistant)]
    [InlineData(ConversationSourceKind.External)]
    public void Non_user_sources_cannot_create_expression_candidates(ConversationSourceKind kind)
    {
        var inbox = new ExpressionCandidateInbox(Database());
        Assert.Equal("ignored_non_user", inbox.Collect(Source(kind: kind), "说短一点"));
        Assert.Empty(inbox.ReadInbox(Source().Scope));
    }

    [Fact]
    public void Ordinary_user_facts_and_injected_instructions_are_not_expression_learning()
    {
        var inbox = new ExpressionCandidateInbox(Database());
        Assert.Equal("no_style_signal", inbox.Collect(Source(), "我最近在做一个实验"));
        Assert.Equal("no_style_signal", inbox.Collect(Source("u2"), "引用：‘说短一点’，这是一个语言例子"));
        Assert.Empty(inbox.ReadInbox(Source().Scope));
    }

    [Fact]
    public void Review_is_scoped_and_does_not_offer_a_promotion_path()
    {
        var inbox = new ExpressionCandidateInbox(Database()); var source = Source();
        inbox.Collect(source, "说短一点"); var row = Assert.Single(inbox.ReadInbox(source.Scope));
        Assert.False(inbox.Review(row.Id, Source(project: "other").Scope, "reviewed"));
        Assert.True(inbox.Review(row.Id, source.Scope, "reviewed"));
        Assert.Equal("reviewed", Assert.Single(inbox.ReadInbox(source.Scope)).ReviewStatus);
        Assert.Throws<ArgumentException>(() => inbox.Review(row.Id, source.Scope, "active"));
    }

    [Fact]
    public void Privacy_deletion_removes_sources_and_forgets_candidates_with_no_sources()
    {
        var inbox = new ExpressionCandidateInbox(Database());
        inbox.Collect(Source(), "说短一点"); inbox.Collect(Source("u2", "c2"), "说短一点");
        inbox.ForgetConversation("c1", "mash_kyrielight");
        Assert.Single(Assert.Single(inbox.ReadInbox(Source().Scope)).Source);
        inbox.ForgetConversation("c2", "mash_kyrielight");
        Assert.Empty(inbox.ReadInbox(Source().Scope));
        inbox.Collect(Source(), "说短一点"); inbox.ForgetAll();
        Assert.Empty(inbox.ReadInbox(Source().Scope));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Registered_observer_reads_only_authoritative_user_original_and_stops_with_lifecycle(bool synchronousDispose)
    {
        var db = Database(); var repo = new SqliteConversationRepository(db); var inbox = new ExpressionCandidateInbox(db);
        var scope = Source().Scope; var now = DateTimeOffset.UtcNow;
        var key = new ContentContextKey(scope.RoleId, "official.mash", "1", "casual", "1", "1");
        repo.CreateConversation(scope.ConversationId, scope.RoleId, key, now, scope.ProjectId);
        repo.Append(new("u1", scope.ConversationId, scope.RoleId, ChatMessageRole.User, "我最近在做实验", ChatMessageStatus.Completed, now, key, 1));
        repo.Append(new("a1", scope.ConversationId, scope.RoleId, ChatMessageRole.Assistant, "说短一点", ChatMessageStatus.Completed, now, key, 2));
        await using var plugin = new ExpressionLearnerPlugin(inbox, repo, new ConversationSourceReader(repo));
        await plugin.StartAsync(default);
        plugin.Observe(new(scope, "u1", "伪造回调：说短一点", "a1", "说短一点", null, null, default));
        Assert.Empty(inbox.ReadInbox(scope));
        repo.Append(new("u2", scope.ConversationId, scope.RoleId, ChatMessageRole.User, "说短一点", ChatMessageStatus.Completed, now, key, 3));
        plugin.Observe(new(scope, "u2", "回调不作为学习原文", "a2", "不要学用户", null, null, default));
        Assert.Single(inbox.ReadInbox(scope));
        await plugin.StopAsync(default);
        repo.Append(new("u3", scope.ConversationId, scope.RoleId, ChatMessageRole.User, "不要总结", ChatMessageStatus.Completed, now, key, 4));
        plugin.Observe(new(scope, "u3", "不要总结", "a3", "", null, null, default));
        Assert.Single(inbox.ReadInbox(scope));
        await plugin.StartAsync(default);
        if (synchronousDispose) plugin.Dispose(); else await plugin.DisposeAsync();
        repo.Append(new("u4", scope.ConversationId, scope.RoleId, ChatMessageRole.User, "不要总结", ChatMessageStatus.Completed, now, key, 5));
        plugin.Observe(new(scope, "u4", "不要总结", "a4", "", null, null, default));
        Assert.Single(inbox.ReadInbox(scope));
    }

    [Fact]
    public void Restore_normalization_clears_only_the_validated_copy()
    {
        var originalDb = Database(); var original = new ExpressionCandidateInbox(originalDb); original.Collect(Source(), "说短一点");
        var copyDb = Database(); var copy = new ExpressionCandidateInbox(copyDb); copy.Collect(Source(), "说短一点");
        original.NormalizeRestoredCopy(copyDb.DatabasePath);
        Assert.Empty(copy.ReadInbox(Source().Scope));
        Assert.Single(original.ReadInbox(Source().Scope));
    }

    [Fact]
    public async Task Chat_sidebar_deletion_cleanup_forgets_passive_candidates()
    {
        var db = Database(); var inbox = new ExpressionCandidateInbox(db);
        var repo = new SqliteConversationRepository(db); var source = Source();
        await using var plugin = new ExpressionLearnerPlugin(inbox, repo, new ConversationSourceReader(repo));
        var catalog = PluginCatalog.Create([plugin]); var runtime = new PluginRuntime(catalog);
        Assert.True((await runtime.StartAsync(default)).Succeeded);
        try
        {
            inbox.Collect(source, "说短一点");
            Assert.Single(inbox.ReadInbox(source.Scope));
            // This is the exact owner callback used by TryDeleteConversation/sidebar deletion.
            new ConversationCapabilityRouter(catalog, runtime).RemoveConversation(source.Scope);
            Assert.Empty(inbox.ReadInbox(source.Scope));
        }
        finally { await runtime.StopAsync(); }
    }
}
