using System.Security.Cryptography;
using System.Text;
using FgoPet.Core.Dialogue;
using FgoPet.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace FgoPet.Infrastructure.Dialogue;

/// <summary>Stores the versioned content context used by a conversation turn.</summary>
public sealed class SqliteContentBindingRepository
{
    private readonly RuntimeDatabase _database;

    public SqliteContentBindingRepository(RuntimeDatabase database) => _database = database;

    public string Upsert(
        ContentContextKey context,
        string personaHash,
        string knowledgeHash,
        DateTimeOffset createdAtUtc)
    {
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        var bindingId = Upsert(connection, transaction, context, personaHash, knowledgeHash, createdAtUtc);
        transaction.Commit();
        return bindingId;
    }

    internal static string Upsert(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ContentContextKey context,
        string personaHash,
        string knowledgeHash,
        DateTimeOffset createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(context);
        var normalizedPersonaHash = NormalizeHash(personaHash);
        var normalizedKnowledgeHash = NormalizeHash(knowledgeHash);
        var bindingId = ComputeBindingId(context, normalizedPersonaHash, normalizedKnowledgeHash);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO content_bindings(
              binding_id, servant_id, package_id, package_version, appearance_id,
              persona_version, knowledge_version, persona_hash, knowledge_hash, created_at_utc)
            VALUES($id, $servant, $package, $package_version, $appearance,
              $persona_version, $knowledge_version, $persona_hash, $knowledge_hash, $created)
            """;
        command.Parameters.AddWithValue("$id", bindingId);
        command.Parameters.AddWithValue("$servant", context.ServantId);
        command.Parameters.AddWithValue("$package", context.PackageId);
        command.Parameters.AddWithValue("$package_version", context.PackageVersion);
        command.Parameters.AddWithValue("$appearance", context.AppearanceId);
        command.Parameters.AddWithValue("$persona_version", context.PersonaVersion);
        command.Parameters.AddWithValue("$knowledge_version", context.KnowledgeVersion);
        command.Parameters.AddWithValue("$persona_hash", normalizedPersonaHash);
        command.Parameters.AddWithValue("$knowledge_hash", normalizedKnowledgeHash);
        command.Parameters.AddWithValue("$created", createdAtUtc.ToString("O"));
        command.ExecuteNonQuery();
        return bindingId;
    }

    internal static string NormalizeHash(string? hash) =>
        string.IsNullOrWhiteSpace(hash) ? "unknown" : hash.Trim();

    private static string ComputeBindingId(ContentContextKey context, string personaHash, string knowledgeHash)
    {
        var material = string.Join('\n',
            context.ServantId,
            context.PackageId,
            context.PackageVersion,
            context.AppearanceId,
            context.PersonaVersion,
            context.KnowledgeVersion,
            personaHash,
            knowledgeHash);
        return "binding-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }
}
