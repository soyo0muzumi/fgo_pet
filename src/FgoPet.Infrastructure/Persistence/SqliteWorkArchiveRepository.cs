using System.Globalization;
using System.Text.Json;
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
                INSERT INTO work_archives(archive_id, archive_date, source_types, title, started_on, completed_on, summary, outcomes, created_at_utc)
                VALUES($id, $date, $sources, $title, $started, $completed, $summary, $outcomes, $created)
                ON CONFLICT(archive_id) DO UPDATE SET
                  archive_date=excluded.archive_date, source_types=excluded.source_types,
                  title=excluded.title, started_on=excluded.started_on, completed_on=excluded.completed_on,
                  summary=excluded.summary, outcomes=excluded.outcomes, created_at_utc=excluded.created_at_utc
                """;
            insert.Parameters.AddWithValue("$id", archive.ArchiveId);
            insert.Parameters.AddWithValue("$date", archive.ArchiveDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            insert.Parameters.AddWithValue("$sources", string.Join("\n", archive.SourceTypes));
            insert.Parameters.AddWithValue("$title", archive.Title);
            insert.Parameters.AddWithValue("$started", archive.StartedOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
            insert.Parameters.AddWithValue("$completed", archive.CompletedOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
            insert.Parameters.AddWithValue("$summary", archive.Summary);
            insert.Parameters.AddWithValue("$outcomes", JsonSerializer.Serialize(archive.Outcomes));
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
        command.CommandText = "SELECT archive_id, archive_date, source_types, title, started_on, completed_on, summary, outcomes, created_at_utc FROM work_archives WHERE archive_id=$id";
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
            archive.CreatedAt,
            archive.Title,
            archive.StartedOn,
            archive.CompletedOn,
            archive.Outcomes);
    }

    public IReadOnlyList<WorkArchive> List()
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT archive_id, archive_date, source_types, title, started_on, completed_on, summary, outcomes, created_at_utc FROM work_archives ORDER BY archive_date DESC, created_at_utc DESC";
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
            archive.CreatedAt,
            archive.Title,
            archive.StartedOn,
            archive.CompletedOn,
            archive.Outcomes)).ToArray();
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

    public void SaveLongArchive(LongWorkArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO long_work_archives(archive_id, title, summary, covered_archive_ids, created_at_utc)
            VALUES($id, $title, $summary, $covered, $created)
            ON CONFLICT(archive_id) DO UPDATE SET
              title=excluded.title, summary=excluded.summary,
              covered_archive_ids=excluded.covered_archive_ids, created_at_utc=excluded.created_at_utc
            """;
        command.Parameters.AddWithValue("$id", archive.ArchiveId);
        command.Parameters.AddWithValue("$title", archive.Title);
        command.Parameters.AddWithValue("$summary", archive.Summary);
        command.Parameters.AddWithValue("$covered", string.Join("\n", archive.CoveredArchiveIds));
        command.Parameters.AddWithValue("$created", archive.CreatedAt.ToString("O"));
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<LongWorkArchive> ListLongArchives()
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT archive_id, title, summary, covered_archive_ids, created_at_utc FROM long_work_archives ORDER BY created_at_utc DESC, archive_id";
        using var reader = command.ExecuteReader();
        var result = new List<LongWorkArchive>();
        while (reader.Read())
        {
            result.Add(new LongWorkArchive(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3).Split('\n', StringSplitOptions.RemoveEmptyEntries),
                DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
        }

        return result;
    }

    public void DeleteLongArchive(string archiveId)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM long_work_archives WHERE archive_id=$id";
        command.Parameters.AddWithValue("$id", archiveId);
        command.ExecuteNonQuery();
    }

    private static ArchiveFields ReadArchiveFields(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(2).Split('\n', StringSplitOptions.RemoveEmptyEntries),
        DateOnly.ParseExact(reader.GetString(1), "yyyy-MM-dd", CultureInfo.InvariantCulture),
        reader.GetString(3),
        reader.IsDBNull(4) ? null : DateOnly.ParseExact(reader.GetString(4), "yyyy-MM-dd", CultureInfo.InvariantCulture),
        reader.IsDBNull(5) ? null : DateOnly.ParseExact(reader.GetString(5), "yyyy-MM-dd", CultureInfo.InvariantCulture),
        reader.GetString(6),
        ParseOutcomes(reader.GetString(7)),
        DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));

    private static IReadOnlyList<string> ParseOutcomes(string value)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(value) ?? Array.Empty<string>();
        }
        catch (JsonException)
        {
            return string.IsNullOrWhiteSpace(value) ? Array.Empty<string>() : new[] { value };
        }
    }

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
        string Title,
        DateOnly? StartedOn,
        DateOnly? CompletedOn,
        string Summary,
        IReadOnlyList<string> Outcomes,
        DateTimeOffset CreatedAt);
}
