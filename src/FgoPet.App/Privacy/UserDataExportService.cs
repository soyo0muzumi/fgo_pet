using System.IO.Compression;
using System.IO;
using System.Text.Json;
using System.Text.Encodings.Web;
using FgoPet.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace FgoPet.App.Privacy;

/// <summary>
/// Exports the user-created dialogue and memory records. The allow-list is
/// deliberate: credentials, prompt payloads, raw pack/story files, and local
/// absolute paths are not export data.
/// </summary>
public sealed class UserDataExportService
{
    private readonly RuntimeDatabase _database;

    public UserDataExportService(RuntimeDatabase database) =>
        _database = database ?? throw new ArgumentNullException(nameof(database));

    public async Task ExportAsync(string destinationPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        cancellationToken.ThrowIfCancellationRequested();
        var export = ReadSafeExport(cancellationToken);
        var fullPath = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var archive = ZipFile.Open(fullPath, ZipArchiveMode.Create);
        var entry = archive.CreateEntry("data.json", CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await JsonSerializer.SerializeAsync(
            stream,
            export.ToJsonObject(),
            new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping },
            cancellationToken);
    }

    private ExportDocument ReadSafeExport(CancellationToken cancellationToken)
    {
        using var connection = _database.Open();
        return new ExportDocument(
            1,
            ReadRows(connection, """
                SELECT conversation_id, servant_id, created_at_utc, updated_at_utc, status, current_binding_id
                FROM conversations ORDER BY created_at_utc, conversation_id
                """, cancellationToken),
            ReadRows(connection, """
                SELECT message_id, conversation_id, servant_id, sequence, role, text, status, created_at_utc, binding_id
                FROM chat_messages ORDER BY conversation_id, sequence
                """, cancellationToken),
            ReadRows(connection, """
                SELECT summary_id, conversation_id, servant_id, summary_text, covered_through_sequence,
                       covered_through_message_id, created_at_utc, updated_at_utc, binding_id
                FROM conversation_summaries ORDER BY conversation_id, updated_at_utc
                """, cancellationToken),
            ReadRows(connection, """
                SELECT candidate_id, conversation_id, source_message_id, servant_id, appearance_id,
                       candidate_text, status, created_at_utc, reviewed_at_utc
                FROM memory_candidates ORDER BY servant_id, created_at_utc, candidate_id
                """, cancellationToken),
            ReadRows(connection, """
                SELECT memory_id, servant_id, memory_text, is_enabled, source_candidate_id,
                       created_at_utc, updated_at_utc
                FROM memories ORDER BY servant_id, updated_at_utc, memory_id
                """, cancellationToken),
            ReadRows(connection, """
                SELECT binding_id, servant_id, package_id, package_version, appearance_id,
                       persona_version, knowledge_version, persona_hash, knowledge_hash,
                       created_at_utc
                FROM content_bindings ORDER BY servant_id, created_at_utc, binding_id
                """, cancellationToken),
            ReadRows(connection, """
                SELECT todo_id, title, description, priority, due_at_utc, status,
                       created_at_utc, updated_at_utc, completed_at_utc
                FROM todo_items ORDER BY created_at_utc, todo_id
                """, cancellationToken),
            ReadRows(connection, """
                SELECT archive_id, archive_date, source_types, summary, created_at_utc
                FROM work_archives ORDER BY archive_date, created_at_utc, archive_id
                """, cancellationToken));
    }

    private static List<IReadOnlyDictionary<string, object?>> ReadRows(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var index = 0; index < reader.FieldCount; index++)
            {
                row[reader.GetName(index)] = reader.IsDBNull(index) ? null : reader.GetValue(index);
            }

            rows.Add(row);
        }

        return rows;
    }

    private sealed record ExportDocument(
        int ExportVersion,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> Conversations,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> Messages,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> Summaries,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> MemoryCandidates,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> Memories,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> ContentBindings,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> Todos,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> WorkArchives)
    {
        public IDictionary<string, object?> ToJsonObject() => new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["export_version"] = ExportVersion,
            ["conversations"] = Conversations,
            ["messages"] = Messages,
            ["summaries"] = Summaries,
            ["memory_candidates"] = MemoryCandidates,
            ["memories"] = Memories,
            ["content_bindings"] = ContentBindings,
            ["todos"] = Todos,
            ["work_archives"] = WorkArchives,
        };
    }
}
