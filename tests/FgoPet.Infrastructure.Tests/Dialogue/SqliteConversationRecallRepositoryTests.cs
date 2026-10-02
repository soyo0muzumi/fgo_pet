using System.IO;
using FgoPet.Core.Dialogue;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.Infrastructure.Persistence;
using Xunit;

namespace FgoPet.Infrastructure.Tests.Dialogue;

public sealed class SqliteConversationRecallRepositoryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"fgo-recall-{Guid.NewGuid():N}.db");
    private readonly RuntimeDatabase _database;
    private readonly SqliteConversationRepository _conversations;
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-23T08:00:00Z");
    public SqliteConversationRecallRepositoryTests()
    {
        _database = new(_path, pooling: false);
        new RuntimeDatabaseMigrator(_database).Migrate();
        _conversations = new(_database);
    }

    [Fact]
    public void Chinese_messages_are_immediate_scoped_and_current_conversation_excluded()
    {
        Seed("a", "mash", "project-a", "下次先完成导师面谈计划");
        Seed("b", "mash", "project-b", "下次先完成导师面谈计划");
        Seed("other", "other", "project-a", "下次先完成导师面谈计划");
        Seed("legacy", "mash", null, "下次先完成导师面谈计划");
        var recall = new SqliteConversationRecallRepository(_database);
        var hit = Assert.Single(recall.Discover(new("mash", "project-a"), "new", "导师面谈"));
        Assert.Equal("a-m1", hit.Anchor.MessageId);
        Assert.Empty(recall.Discover(new("mash", "project-a"), "a", "导师面谈"));
        Assert.Equal("legacy", Assert.Single(recall.Discover(new("mash", null), "new", "导师面谈")).Anchor.ConversationId);
        var reopened = new SqliteConversationRepository(new RuntimeDatabase(_path, pooling: false));
        Assert.Equal("project-a", reopened.ListConversations("mash").Single(c => c.ConversationId == "a").ProjectId);
        Assert.Single(reopened.ReadPage(new ConversationScope("mash", "project-b")).Items);
    }

    [Fact]
    public void Short_chinese_queries_are_bounded_and_match_operators_remain_literal()
    {
        Seed("a", "mash", null, "记忆内容 OR * \"导师\"");
        var recall = new SqliteConversationRecallRepository(_database);
        Assert.Single(recall.Discover(new("mash", null), "new", "记忆"));
        Assert.Single(recall.Discover(new("mash", null), "new", "OR *"));
        Assert.Empty(recall.Discover(new("mash", null), "new", "内容 OR nonexistent"));
        for (var i = 0; i < 25; i++) Seed("next-" + i, "mash", null, "another topic");
        Assert.Empty(recall.Discover(new("mash", null), "new", "记忆"));
        Assert.InRange(recall.Browse(new("mash", null), "new").Count, 1, 10);
    }

    [Fact]
    public void Anchored_pages_preserve_emoji_and_reject_other_scope_or_deleted_source()
    {
        var full = string.Concat(Enumerable.Repeat("😀", 6000));
        Seed("a", "mash", "project-a", full);
        var recall = new SqliteConversationRecallRepository(_database);
        var scope = new ConversationScope("mash", "project-a");
        var hit = Assert.Single(recall.Browse(scope, "new"));
        var page = recall.Read(scope, hit.Anchor, 799);
        Assert.True(Assert.Single(page.Items).IsTruncated);
        Assert.NotNull(page.Next);
        Assert.Throws<ArgumentException>(() => recall.Read(new("mash", "project-b"), hit.Anchor, 799, page.Next));
        var text = string.Concat(page.Items.Select(item => item.Excerpt));
        while (page.Next is not null)
        {
            page = recall.Read(scope, hit.Anchor, 799, page.Next);
            text += string.Concat(page.Items.Select(item => item.Excerpt));
        }
        Assert.Equal(full, text);
        _conversations.DeleteConversation("a", "mash");
        Assert.Empty(recall.Read(scope, hit.Anchor).Items);
        Assert.Empty(recall.Discover(scope, "new", "😀😀😀"));
    }

    [Fact]
    public void Search_and_revision_follow_update_delete_and_filter_non_completed_roles()
    {
        Seed("a", "mash", null, "原始面谈计划");
        using var db = _database.Open();
        using var command = db.CreateCommand();
        command.CommandText = "UPDATE chat_messages SET text='修订无线实验计划' WHERE message_id='a-m1'";
        command.ExecuteNonQuery();
        var recall = new SqliteConversationRecallRepository(_database);
        Assert.Empty(recall.Discover(new("mash", null), "new", "原始面谈"));
        Assert.Single(recall.Discover(new("mash", null), "new", "无线实验"));
        command.CommandText = "SELECT context_revision FROM conversations WHERE conversation_id='a'";
        Assert.Equal(2L, command.ExecuteScalar());
        command.CommandText = "UPDATE chat_messages SET status='failed' WHERE message_id='a-m1'";
        command.ExecuteNonQuery();
        Assert.Empty(recall.Discover(new("mash", null), "new", "无线实验"));
        Assert.Empty(recall.Browse(new("mash", null), "new"));
    }

    private void Seed(string id, string servant, string? project, string text)
    {
        var key = new ContentContextKey(servant, "pack", "1", "default", "1", "1");
        _conversations.CreateConversation(id, servant, key, Now.AddSeconds(_conversations.ListConversations(servant).Count), project, project);
        _conversations.Append(new(id + "-m1", id, servant, ChatMessageRole.User, text,
            ChatMessageStatus.Completed, Now.AddSeconds(_conversations.ListConversations(servant).Count), key, 1));
    }
    public void Dispose() { foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(_path + suffix); }
}
