using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.Infrastructure.Persistence;
using Xunit;

namespace FgoPet.Infrastructure.Tests.Dialogue;

public sealed class StickerPersistenceTests : IDisposable
{
    [Fact]
    public void Collected_asset_requires_a_host_catalog_and_roundtrips_as_presentation_metadata()
    {
        var database = new RuntimeDatabase(path, pooling: false);
        new RuntimeDatabaseMigrator(database).Migrate();
        var asset = new StickerAsset("web." + new string('a', 48), "laugh", "大笑", "images/" + new string('A', 64) + ".png", new string('A', 64), true);
        var source = new Catalog(asset);
        var candidate = new StickerCandidate(asset.Id, asset.Intent, "enhance", "high", "playful", "after");
        var repo = new SqliteConversationRepository(database);
        repo.CreateConversation("chat", context.ServantId, context, DateTimeOffset.UtcNow);
        Assert.Null(repo.AppendReply(Message("unresolved", 1), candidate, false).Sticker);
        var key = Enumerable.Range(0, 200).Select(i => "dynamic-" + i)
            .First(k => StickerPolicy.Select(candidate, [], k, "完整正文", catalog: source.ReadAssets()) is not null);
        var admitted = new SqliteConversationRepository(database, source).AppendReply(Message(key, 2), candidate, false);
        Assert.Equal(asset.Id, admitted.Sticker!.AssetId);
        Assert.Equal(admitted.Sticker, new SqliteConversationRepository(database, source).LoadMessages("chat", context.ServantId).Last().Sticker);
    }

    private sealed class Catalog(StickerAsset asset) : IStickerCatalogSource
    { public IReadOnlyList<StickerAsset> ReadAssets() => [.. StickerCatalog.Assets, asset]; }

    private readonly string path = Path.Combine(Path.GetTempPath(), "fgopet-stickers-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly ContentContextKey context = new("mash_kyrielight", "official.mash", "1", "default", "1", "1");
    private readonly StickerCandidate happy = new("mash.happy", "happy", "enhance", "high", "social", "after");

    [Fact]
    public void Selection_survives_reopen_and_migration_is_additive_and_idempotent()
    {
        var database = new RuntimeDatabase(path, pooling: false);
        new RuntimeDatabaseMigrator(database).Migrate();
        var repo = new SqliteConversationRepository(database);
        repo.CreateConversation("chat", context.ServantId, context, DateTimeOffset.UtcNow);
        repo.Append(Message("old", 1));
        var key = Key(happy);
        var selected = repo.AppendReply(Message(key, 2), happy, false);
        Assert.NotNull(selected.Sticker);
        Assert.Equal("完整正文", selected.Text);
        var reopened = new SqliteConversationRepository(new RuntimeDatabase(path, pooling: false));
        var messages = reopened.LoadMessages("chat", context.ServantId);
        Assert.Null(messages[0].Sticker);
        Assert.Equal(selected.Sticker, messages[1].Sticker);
        Assert.Null(reopened.AppendReply(Message("cooldown", 3), happy, false).Sticker);
        using var connection = database.Open();
        Assert.Equal(RuntimeDatabaseMigrator.CurrentSchemaVersion, RuntimeDatabaseMigrator.ReadVersion(connection));
        reopened.DeleteConversation("chat", context.ServantId);
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM chat_reply_presentations";
        Assert.Equal(0L, query.ExecuteScalar());
    }

    [Fact]
    public void Successful_replies_advance_budget_but_failures_and_other_conversations_do_not()
    {
        var database = new RuntimeDatabase(path, pooling: false);
        new RuntimeDatabaseMigrator(database).Migrate();
        var repo = new SqliteConversationRepository(database);
        repo.CreateConversation("chat", context.ServantId, context, DateTimeOffset.UtcNow);
        var first = repo.AppendReply(Message(Key(happy), 1), happy, false);
        Assert.NotNull(first.Sticker);
        repo.Append(new("failed", "chat", context.ServantId, ChatMessageRole.Assistant, "", ChatMessageStatus.Failed,
            DateTimeOffset.UtcNow, context, 2));
        var curious = new StickerCandidate("mash.shine", "excited", "enhance", "high", "social", "before");
        Assert.Null(repo.AppendReply(Message("still-cooling", 3), curious, false).Sticker);
        repo.AppendReply(Message("plain-1", 4), null, false);
        repo.AppendReply(Message("plain-2", 5), null, false);
        Assert.NotNull(repo.AppendReply(Message(Key(curious), 6), curious, false).Sticker);
        repo.AppendReply(Message("plain-3", 7), null, false);
        repo.AppendReply(Message("plain-4", 8), null, false);
        repo.AppendReply(Message("plain-5", 9), null, false);
        var surprised = new StickerCandidate("mash.shocked", "surprise", "enhance", "high", "social", "after");
        Assert.Null(repo.AppendReply(Message(Key(surprised), 10), surprised, false).Sticker);
        repo.CreateConversation("other", context.ServantId, context, DateTimeOffset.UtcNow);
        var other = new ChatMessage("other-" + Key(happy), "other", context.ServantId, ChatMessageRole.Assistant,
            "完整正文", ChatMessageStatus.Completed, DateTimeOffset.UtcNow, context, 1);
        // Find a deterministic admission for this independent conversation.
        var otherKey = Enumerable.Range(0, 100).Select(i => "other-" + i)
            .First(k => StickerPolicy.Select(happy, [], k, other.Text) is not null);
        other = new(otherKey, "other", context.ServantId, ChatMessageRole.Assistant, other.Text, other.Status, other.CreatedAtUtc, context, 1);
        Assert.NotNull(repo.AppendReply(other, happy, false).Sticker);
    }

    [Fact]
    public void Host_blocks_tool_results_and_other_roles_even_when_candidate_is_valid()
    {
        var database = new RuntimeDatabase(path, pooling: false);
        new RuntimeDatabaseMigrator(database).Migrate();
        var repo = new SqliteConversationRepository(database);
        repo.CreateConversation("chat", context.ServantId, context, DateTimeOffset.UtcNow);
        Assert.Null(repo.AppendReply(Message(Key(happy), 1), happy, true).Sticker);
        var other = new ContentContextKey("other", "official.mash", "1", "default", "1", "1");
        repo.CreateConversation("other", "other", other, DateTimeOffset.UtcNow);
        Assert.Null(repo.AppendReply(new("other-id", "other", "other", ChatMessageRole.Assistant, "正文", ChatMessageStatus.Completed,
            DateTimeOffset.UtcNow, other, 1), happy, false).Sticker);
    }

    private ChatMessage Message(string id, int sequence) => new(id, "chat", context.ServantId, ChatMessageRole.Assistant,
        "完整正文", ChatMessageStatus.Completed, DateTimeOffset.UtcNow, context, sequence);
    private static string Key(StickerCandidate candidate) => Enumerable.Range(0, 100).Select(i => candidate.Intent + i)
        .First(k => StickerPolicy.Select(candidate, [], k, "完整正文") is not null);
    public void Dispose() { foreach (var suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(path + suffix)) File.Delete(path + suffix); }
}
