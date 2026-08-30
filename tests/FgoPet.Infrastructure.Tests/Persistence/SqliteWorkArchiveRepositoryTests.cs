using FgoPet.Core.Archives;
using FgoPet.Core.Todo;
using FgoPet.Infrastructure.Persistence;
using Xunit;

namespace FgoPet.Infrastructure.Tests.Persistence;

public sealed class SqliteWorkArchiveRepositoryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"fgo-archive-{Guid.NewGuid():N}.db");

    [Fact]
    public void Confirm_archive_writes_summary_and_removes_only_covered_completed_details()
    {
        var database = CreateDatabase();
        var todos = new SqliteTodoRepository(database);
        var archives = new SqliteWorkArchiveRepository(database);
        var at = DateTimeOffset.Parse("2026-08-30T08:00:00Z");
        todos.Save(new TodoItem("todo-1", "Covered", null, TodoPriority.Normal, null, at, at)
            .Activate(at.AddMinutes(1)).Complete(at.AddMinutes(2)));
        todos.Save(new TodoItem("todo-2", "Not covered", null, TodoPriority.Normal, null, at, at)
            .Activate(at.AddMinutes(1)).Complete(at.AddMinutes(2)));
        todos.Save(new TodoItem("todo-3", "Still planned", null, TodoPriority.Normal, null, at, at));

        var archive = new WorkArchive("archive-1", new[] { "todo-1" }, new[] { "codex" }, DateOnly.Parse("2026-08-30"), "Delivered.", at);
        archives.Confirm(archive);

        Assert.NotNull(archives.Get("archive-1"));
        Assert.Null(todos.Get("todo-1"));
        Assert.NotNull(todos.Get("todo-2"));
        Assert.NotNull(todos.Get("todo-3"));
        Assert.Equal(new[] { "todo-1" }, archives.LoadCoveredTodoKeys("archive-1"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var file = _path + suffix;
            if (File.Exists(file)) File.Delete(file);
        }
    }

    private RuntimeDatabase CreateDatabase()
    {
        var database = new RuntimeDatabase(_path);
        new RuntimeDatabaseMigrator(database).Migrate();
        return database;
    }
}
