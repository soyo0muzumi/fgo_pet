using FgoPet.Core.Agents;
using FgoPet.Infrastructure.Backup;
using FgoPet.Infrastructure.Persistence;
using Xunit;

namespace FgoPet.Infrastructure.Tests.Backup;

public sealed class BackupDatabaseNormalizerTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"fgo-normalizer-{Guid.NewGuid():N}.db");

    [Fact]
    public void Normalizes_only_non_terminal_executions_and_preserves_identity_and_remote_task_id()
    {
        var database = new RuntimeDatabase(_databasePath);
        new RuntimeDatabaseMigrator(database).Migrate();
        var repository = new SqliteAgentRepository(database);
        var at = DateTimeOffset.Parse("2026-09-02T01:02:03Z");
        repository.SaveExecution(new AgentExecution("dispatching", "todo-1", "codex", "instance-1", "task-1", "request-1", at, AgentExecutionStatus.Dispatching, remoteTaskId: "remote-1"));
        repository.SaveExecution(new AgentExecution("active", "todo-2", "codex", "instance-1", "task-2", "request-2", at, AgentExecutionStatus.Active, startedAt: at.AddMinutes(-1), remoteTaskId: "remote-2"));
        repository.SaveExecution(new AgentExecution("attention", "todo-3", "codex", "instance-1", "task-3", "request-3", at, AgentExecutionStatus.Attention, startedAt: at.AddMinutes(-2), remoteTaskId: "remote-3"));
        repository.SaveExecution(new AgentExecution("completed", "todo-4", "codex", "instance-1", "task-4", "request-4", at, AgentExecutionStatus.Completed, startedAt: at.AddMinutes(-3), endedAt: at, remoteTaskId: "remote-4"));

        var changed = BackupDatabaseNormalizer.Normalize(database, at.AddMinutes(1));

        Assert.Equal(3, changed);
        foreach (var id in new[] { "dispatching", "active", "attention" })
        {
            var execution = repository.GetExecution(id)!;
            Assert.Equal(AgentExecutionStatus.DispatchOutcomeUnknown, execution.Status);
            Assert.Equal(id switch
            {
                "dispatching" => "remote-1",
                "active" => "remote-2",
                _ => "remote-3",
            }, execution.RemoteTaskId);
            Assert.Equal(id switch
            {
                "dispatching" => "task-1",
                "active" => "task-2",
                _ => "task-3",
            }, execution.TaskId);
            Assert.Equal(at.AddMinutes(1), execution.UpdatedAt);
        }

        var completed = repository.GetExecution("completed")!;
        Assert.Equal(AgentExecutionStatus.Completed, completed.Status);
        Assert.Equal("remote-4", completed.RemoteTaskId);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
