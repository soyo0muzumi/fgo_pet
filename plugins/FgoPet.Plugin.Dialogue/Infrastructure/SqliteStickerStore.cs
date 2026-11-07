using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace FgoPet.Infrastructure.Dialogue;

/// <summary>Optional, additive presentation metadata. Deletes cascade with the original message.</summary>
internal static class SqliteStickerStore
{
    internal static void EnsureSchema(RuntimeDatabase database) => new RuntimeDatabaseMigrator(database).MigrateModule("dialogue-stickers",
        [new(1, """
            CREATE TABLE chat_reply_presentations(
              message_id TEXT PRIMARY KEY REFERENCES chat_messages(message_id) ON DELETE CASCADE,
              asset_id TEXT NULL CHECK(asset_id IS NULL OR length(asset_id) BETWEEN 1 AND 64),
              intent TEXT NULL CHECK(intent IS NULL OR length(intent) BETWEEN 1 AND 64),
              position TEXT NULL CHECK(position IS NULL OR position IN ('before','after')),
              CHECK((asset_id IS NULL AND intent IS NULL AND position IS NULL)
                 OR (asset_id IS NOT NULL AND intent IS NOT NULL AND position IS NOT NULL)));
            """)]);

    internal static StickerSelection? Choose(SqliteConnection connection, SqliteTransaction transaction, ChatMessage message,
        StickerCandidate? candidate, bool blocked, IStickerCatalogSource? catalog = null)
    {
        if (!StickerPolicy.IsMash(message.ServantId, message.ContentContext.PackageId) || message.Role != ChatMessageRole.Assistant
            || message.Status != ChatMessageStatus.Completed) return null;
        using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = """
            SELECT p.asset_id,p.intent,p.position FROM chat_reply_presentations p
            JOIN chat_messages m ON m.message_id=p.message_id
            WHERE m.conversation_id=$conversation AND m.servant_id=$role AND m.role='assistant' AND m.status='completed'
            ORDER BY m.sequence DESC LIMIT 10
            """;
        query.Parameters.AddWithValue("$conversation", message.ConversationId);
        query.Parameters.AddWithValue("$role", message.ServantId);
        using var reader = query.ExecuteReader();
        var history = new List<StickerSelection?>();
        while (reader.Read()) history.Add(reader.IsDBNull(0) ? null : new(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return StickerPolicy.Select(candidate, history, message.MessageId, message.Text, blocked, catalog?.ReadAssets());
    }

    internal static void Save(SqliteConnection connection, SqliteTransaction transaction, ChatMessage message)
    {
        if (message.Role != ChatMessageRole.Assistant || message.Status != ChatMessageStatus.Completed
            || message.Text is "[工具调用]" or "[工具调用：待办提案]") return;
        var sticker = message.Sticker;
        if (sticker is not null && (!StickerPolicy.IsMash(message.ServantId, message.ContentContext.PackageId)
            || (StickerCatalog.Find(sticker.AssetId)?.Intent != sticker.Intent
                && !(StickerPolicy.IsWebAssetId(sticker.AssetId) && StickerPolicy.IsWebIntent(sticker.Intent)))
            || sticker.Position is not ("before" or "after"))) throw new ArgumentException("Invalid sticker presentation.");
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO chat_reply_presentations(message_id,asset_id,intent,position) VALUES($id,$asset,$intent,$position)";
        insert.Parameters.AddWithValue("$id", message.MessageId);
        insert.Parameters.AddWithValue("$asset", (object?)sticker?.AssetId ?? DBNull.Value);
        insert.Parameters.AddWithValue("$intent", (object?)sticker?.Intent ?? DBNull.Value);
        insert.Parameters.AddWithValue("$position", (object?)sticker?.Position ?? DBNull.Value);
        insert.ExecuteNonQuery();
    }
}
