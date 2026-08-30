using FgoPet.Core.Events;
using FgoPet.Core.Focus;
using FgoPet.Infrastructure.Events;
using FgoPet.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FgoPet.Infrastructure.Tests.Events;

public sealed class SqliteEventStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"fgo-event-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var file = _path + suffix;
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }

    [Fact]
    public void TryInsert_persists_optional_source_metadata()
    {
        var database = new RuntimeDatabase(_path);
        new RuntimeDatabaseMigrator(database).Migrate();
        var store = new SqliteEventStore(database);
        var runtimeEvent = new RuntimeEvent(
            "codex-task-1-7",
            "external-codex",
            "task_completed",
            DateTimeOffset.Parse("2026-08-30T09:00:00Z"),
            0,
            FocusPhase.Focus,
            "servant-mash",
            0,
            0,
            1,
            Source: RuntimeEventSource.Codex,
            SubjectId: "task-1",
            Summary: "任务已完成",
            IsPrivate: true);

        Assert.True(store.TryInsert(runtimeEvent));

        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT source, subject_id, summary, is_private FROM runtime_events WHERE event_id = $id";
        command.Parameters.AddWithValue("$id", runtimeEvent.EventId);
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(RuntimeEventSource.Codex, reader.GetString(0));
        Assert.Equal("task-1", reader.GetString(1));
        Assert.Equal("任务已完成", reader.GetString(2));
        Assert.Equal(1L, reader.GetInt64(3));
    }

    [Fact]
    public void TryInsert_remains_idempotent_with_extended_metadata()
    {
        var database = new RuntimeDatabase(_path);
        new RuntimeDatabaseMigrator(database).Migrate();
        var store = new SqliteEventStore(database);
        var runtimeEvent = new RuntimeEvent(
            "event-duplicate",
            "session-1",
            RuntimeEventType.FocusStarted,
            DateTimeOffset.UtcNow,
            1,
            FocusPhase.Focus,
            "servant-mash",
            0,
            0,
            2);

        Assert.True(store.TryInsert(runtimeEvent));
        Assert.False(store.TryInsert(runtimeEvent with { Summary = "ignored" }));
    }
}
