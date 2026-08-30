using System.IO.Compression;
using System.IO;
using System.Text;
using FgoPet.App.Privacy;
using FgoPet.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FgoPet.App.Tests.Services;

public sealed class DataExportServiceTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"fgo-phase4-export-{Guid.NewGuid():N}.db");
    private readonly string _exportPath = Path.Combine(Path.GetTempPath(), $"fgo-phase4-export-{Guid.NewGuid():N}.zip");

    [Fact]
    public async Task Export_includes_todos_and_work_archive_summaries_only()
    {
        var database = new RuntimeDatabase(_databasePath);
        new RuntimeDatabaseMigrator(database).Migrate();
        using (var connection = database.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO todo_items(todo_id, title, description, priority, due_at_utc, status, created_at_utc, updated_at_utc, completed_at_utc)
                VALUES('todo-1', 'Export me', NULL, 'normal', NULL, 'planned', '2026-08-30T00:00:00Z', '2026-08-30T00:00:00Z', NULL);
                INSERT INTO work_archives(archive_id, archive_date, source_types, summary, created_at_utc)
                VALUES('archive-1', '2026-08-30', 'codex', 'Safe summary', '2026-08-30T00:00:00Z');
                """;
            command.ExecuteNonQuery();
        }

        await new UserDataExportService(database).ExportAsync(_exportPath, CancellationToken.None);

        using var archive = ZipFile.OpenRead(_exportPath);
        using var reader = new StreamReader(archive.GetEntry("data.json")!.Open(), Encoding.UTF8);
        var text = await reader.ReadToEndAsync();
        Assert.Contains("Export me", text, StringComparison.Ordinal);
        Assert.Contains("Safe summary", text, StringComparison.Ordinal);
        Assert.DoesNotContain("credentials", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", text, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm", _exportPath })
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
