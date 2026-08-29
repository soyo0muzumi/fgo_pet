using System.Globalization;
using FgoPet.Core.Dialogue;
using FgoPet.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace FgoPet.Infrastructure.Dialogue;

public sealed class SqliteConversationRepository
{
    private readonly RuntimeDatabase _database;

    public SqliteConversationRepository(RuntimeDatabase database) => _database = database;

    public Conversation CreateConversation(
        string conversationId,
        string servantId,
        ContentContextKey contentContext,
        DateTimeOffset createdAtUtc)
    {
        var conversation = new Conversation(conversationId, servantId, createdAtUtc, createdAtUtc, contentContext);
        if (!string.Equals(conversation.ServantId, contentContext.ServantId, StringComparison.Ordinal))
        {
            throw new ArgumentException("The servant ID must match the content context.", nameof(servantId));
        }

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        var bindingId = SqliteContentBindingRepository.Upsert(
            connection,
            transaction,
            contentContext,
            contentContext.PersonaVersion,
            contentContext.KnowledgeVersion,
            createdAtUtc);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO conversations(
              conversation_id, servant_id, created_at_utc, updated_at_utc, status, current_binding_id)
            VALUES($id, $servant, $created, $updated, 'active', $binding)
            """;
        command.Parameters.AddWithValue("$id", conversation.ConversationId);
        command.Parameters.AddWithValue("$servant", conversation.ServantId);
        command.Parameters.AddWithValue("$created", createdAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$updated", createdAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$binding", bindingId);
        command.ExecuteNonQuery();
        transaction.Commit();

        return conversation;
    }

    public void Append(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        var conversationServant = ReadConversationServant(connection, transaction, message.ConversationId);
        if (conversationServant is null)
        {
            throw new KeyNotFoundException($"Conversation '{message.ConversationId}' was not found.");
        }

        if (!string.Equals(conversationServant, message.ServantId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Conversation and message servant IDs do not match.");
        }

        var bindingId = SqliteContentBindingRepository.Upsert(
            connection,
            transaction,
            message.ContentContext,
            message.ContentContext.PersonaVersion,
            message.ContentContext.KnowledgeVersion,
            message.CreatedAtUtc);

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO chat_messages(
                  message_id, conversation_id, servant_id, sequence, role, text, status, created_at_utc, binding_id)
                VALUES($id, $conversation, $servant, $sequence, $role, $text, $status, $created, $binding)
                """;
            command.Parameters.AddWithValue("$id", message.MessageId);
            command.Parameters.AddWithValue("$conversation", message.ConversationId);
            command.Parameters.AddWithValue("$servant", message.ServantId);
            command.Parameters.AddWithValue("$sequence", message.Sequence);
            command.Parameters.AddWithValue("$role", message.Role.ToString().ToLowerInvariant());
            command.Parameters.AddWithValue("$text", message.Text);
            command.Parameters.AddWithValue("$status", message.Status.ToString().ToLowerInvariant());
            command.Parameters.AddWithValue("$created", message.CreatedAtUtc.ToString("O"));
            command.Parameters.AddWithValue("$binding", bindingId);
            command.ExecuteNonQuery();
        }

        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE conversations SET updated_at_utc=$updated, current_binding_id=$binding WHERE conversation_id=$id";
            update.Parameters.AddWithValue("$updated", message.CreatedAtUtc.ToString("O"));
            update.Parameters.AddWithValue("$binding", bindingId);
            update.Parameters.AddWithValue("$id", message.ConversationId);
            update.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public IReadOnlyList<ChatMessage> LoadMessages(string conversationId, string servantId)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT m.message_id, m.conversation_id, m.servant_id, m.role, m.text, m.status,
                   m.created_at_utc, m.sequence,
                   b.servant_id, b.package_id, b.package_version, b.appearance_id,
                   b.persona_version, b.knowledge_version
            FROM chat_messages m
            JOIN conversations c ON c.conversation_id=m.conversation_id
            LEFT JOIN content_bindings b ON b.binding_id=m.binding_id
            WHERE m.conversation_id=$conversation AND c.servant_id=$servant
            ORDER BY m.sequence
            """;
        command.Parameters.AddWithValue("$conversation", conversationId);
        command.Parameters.AddWithValue("$servant", servantId);

        using var reader = command.ExecuteReader();
        var messages = new List<ChatMessage>();
        while (reader.Read())
        {
            if (reader.IsDBNull(8))
            {
                throw new InvalidDataException("A chat message is missing its content binding.");
            }

            var context = new ContentContextKey(
                reader.GetString(8),
                reader.GetString(9),
                reader.GetString(10),
                reader.GetString(11),
                reader.GetString(12),
                reader.GetString(13));
            messages.Add(new ChatMessage(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                ParseRole(reader.GetString(3)),
                reader.GetString(4),
                ParseStatus(reader.GetString(5)),
                ParseUtc(reader.GetString(6)),
                context,
                reader.GetInt32(7)));
        }

        return messages;
    }

    public void DeleteConversation(string conversationId, string servantId)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM conversations WHERE conversation_id=$id AND servant_id=$servant";
        command.Parameters.AddWithValue("$id", conversationId);
        command.Parameters.AddWithValue("$servant", servantId);
        command.ExecuteNonQuery();
    }

    private static string? ReadConversationServant(SqliteConnection connection, SqliteTransaction transaction, string conversationId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT servant_id FROM conversations WHERE conversation_id=$id";
        command.Parameters.AddWithValue("$id", conversationId);
        return command.ExecuteScalar() as string;
    }

    private static ChatMessageRole ParseRole(string value) =>
        Enum.Parse<ChatMessageRole>(value, ignoreCase: true);

    private static ChatMessageStatus ParseStatus(string value) =>
        Enum.Parse<ChatMessageStatus>(value, ignoreCase: true);

    private static DateTimeOffset ParseUtc(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
