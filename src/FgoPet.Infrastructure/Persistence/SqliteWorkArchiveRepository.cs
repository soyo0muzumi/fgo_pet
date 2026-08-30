using System.Globalization;
using FgoPet.Core.Archives;
using Microsoft.Data.Sqlite;

namespace FgoPet.Infrastructure.Persistence;

public sealed class SqliteWorkArchiveRepository : IWorkArchiveRepository
{
    private readonly RuntimeDatabase _database;

    public SqliteWorkArchiveRepository(RuntimeDatabase database) => _database = database;

    public void Confirm(WorkArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        foreach (var todoKey in archive.CoveredTodoKeys)
        {
            using var verify = connection.CreateCommand();
            verify.Transaction = transaction;
            verify.CommandText = "SELECT status FROM todo_items WHERE todo_id=$id";
            verify.Parameters.AddWithValue("$id", todoKey);
            var status = verify.ExecuteScalar() as string;
            if (!string.Equals(status, "completed", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("A work archive may cover only completed Todo items.");
            }
        }

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO work_archives(archive_id, archive_date, source_types, summary, created_at_utc)
                VALUES($id, $date, $sources, $summary, $created)
                ON CONFLICT(archive_id) DO UPDATE SET
                  archive_date=excluded.archive_date, source_types=excluded.source_types,
                  summary=excluded.summary, created_at_utc=excluded.created_at_utc
                """;
            insert.Parameters.AddWithValue("$id", archive.ArchiveId);
            insert.Parameters.AddWithValue("$date", archive.ArchiveDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            insert.Parameters.AddWithValue("$sources", string.Join("\n", archive.SourceTypes));
            insert.Parameters.AddWithValue("$summary", archive.Summary);
            insert.Parameters.AddWithValue("$created", archive.CreatedAt.ToString("O"));
            insert.ExecuteNonQuery();
        }

        using (var clearItems = connection.CreateCommand())
        {
            clearItems.Transaction = transaction;
            clearItems.CommandText = "DELETE FROM work_archive_items WHERE archive_id=$id";
            clearItems.Parameters.AddWithValue("$id", archive.ArchiveId);
            clearItems.ExecuteNonQuery();
        }

        foreach (var todoKey in archive.CoveredTodoKeys)
        {
            using var item = connection.CreateCommand();
            item.Transaction = transaction;
            item.CommandText = "INSERT INTO work_archive_items(archive_id, todo_key) VALUES($archive, $todo)";
            item.Parameters.AddWithValue("$archive", archive.ArchiveId);
            item.Parameters.AddWithValue("$todo", todoKey);
            item.ExecuteNonQuery();
        }

        using (var deleteTodos = connection.CreateCommand())
        {
            deleteTodos.Transaction = transaction;
            deleteTodos.CommandText = $"DELETE FROM todo_items WHERE todo_id IN ({string.Join(",", archive.CoveredTodoKeys.Select((_, index) => "$todo" + index))})";
            for (var index = 0; index < archive.CoveredTodoKeys.Count; index++)
            {
                deleteTodos.Parameters.AddWithValue("$todo" + index, archive.CoveredTodoKeys[index]);
            }

            deleteTodos.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public WorkArchive? Get(string archiveId)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT archive_id, archive_date, source_types, summary, created_at_utc FROM work_archives WHERE archive_id=$id";
        command.Parameters.AddWithValue("$id", archiveId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var archive = ReadArchiveFields(reader);
        reader.Close();
        return new WorkArchive(
            archive.ArchiveId,
            LoadCoveredTodoKeys(connection, archive.ArchiveId),
            archive.SourceTypes,
            archive.ArchiveDate,
            archive.Summary,
            archive.CreatedAt);
    }

    public IReadOnlyList<WorkArchive> List()
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT archive_id, archive_date, source_types, summary, created_at_utc FROM work_archives ORDER BY archive_date DESC, created_at_utc DESC";
        using var reader = command.ExecuteReader();
        var fields = new List<ArchiveFields>();
        while (reader.Read()) fields.Add(ReadArchiveFields(reader));
        reader.Close();
        return fields.Select(archive => new WorkArchive(
            archive.ArchiveId,
            LoadCoveredTodoKeys(connection, archive.ArchiveId),
            archive.SourceTypes,
            archive.ArchiveDate,
            archive.Summary,
            archive.CreatedAt)).ToArray();
    }

    public IReadOnlyList<string> LoadCoveredTodoKeys(string archiveId)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT todo_key FROM work_archive_items WHERE archive_id=$id ORDER BY todo_key";
        command.Parameters.AddWithValue("$id", archiveId);
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    private static ArchiveFields ReadArchiveFields(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(2).Split('\n', StringSplitOptions.RemoveEmptyEntries),
        DateOnly.ParseExact(reader.GetString(1), "yyyy-MM-dd", CultureInfo.InvariantCulture),
        reader.GetString(3),
        DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));

    private static IReadOnlyList<string> LoadCoveredTodoKeys(SqliteConnection connection, string archiveId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT todo_key FROM work_archive_items WHERE archive_id=$id ORDER BY todo_key";
        command.Parameters.AddWithValue("$id", archiveId);
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    private sealed record ArchiveFields(
        string ArchiveId,
        IReadOnlyList<string> SourceTypes,
        DateOnly ArchiveDate,
        string Summary,
        DateTimeOffset CreatedAt);
}
