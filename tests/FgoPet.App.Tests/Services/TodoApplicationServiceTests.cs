using System.IO;
using FgoPet.App.Services;
using FgoPet.Core.Todo;
using FgoPet.Infrastructure.Persistence;
using Xunit;

namespace FgoPet.App.Tests.Services;

public sealed class TodoApplicationServiceTests
{
    [Fact]
    public void Repeated_confirmation_of_one_proposal_creates_only_one_todo()
    {
        var repository = new FakeTodoRepository();
        var service = new TodoApplicationService(repository, TimeProvider.System);
        var proposal = new FgoPet.App.ViewModels.TodoProposalViewModel(
            new FgoPet.App.Dialogue.TodoProposal("Learn Transformer", "1. Attention\n2. Architecture", TodoPriority.Normal, null),
            new FgoPet.App.Dialogue.TodoProposalService(service));
        var first = proposal.Confirm();
        var second = proposal.Confirm();
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, repository.SaveCount);
    }

    [Fact]
    public void Creating_a_todo_persists_it_without_selecting_or_dispatching_an_agent()
    {
        var repository = new FakeTodoRepository();
        var service = new TodoApplicationService(repository, TimeProvider.System);

        var todo = service.Create("Write tests", "Keep the change small.", TodoPriority.High, null);

        Assert.Equal(TodoStatus.Planned, todo.Status);
        Assert.True(todo.CanDispatch);
        Assert.Same(todo, repository.Saved);
    }

    [Fact]
    public void Creating_with_step_titles_creates_one_parent_with_ordered_steps()
    {
        var repository = new FakeTodoRepository();
        var service = new TodoApplicationService(repository, new FixedTimeProvider(DateTimeOffset.Parse("2026-09-13T08:00:00Z")));

        var todo = service.Create("Ship", null, TodoPriority.Normal, null, new[] { "Prepare", "Check" });

        Assert.Equal(2, todo.Steps.Count);
        Assert.Equal(new[] { 0, 1 }, todo.Steps.Select(step => step.Order));
        Assert.All(todo.Steps, step => Assert.StartsWith("step-", step.Id));
        Assert.Equal(todo, repository.Saved);
    }

    [Fact]
    public void Updating_parent_fields_without_steps_preserves_existing_steps()
    {
        var repository = new FakeTodoRepository();
        var service = new TodoApplicationService(repository, TimeProvider.System);
        var original = service.Create("Before", null, TodoPriority.Normal, null, new[] { "Prepare" });

        var edited = service.Update(original.Id, "After", "Keep steps");

        Assert.Equal("After", edited.Title);
        Assert.Equal(original.Steps, edited.Steps);
    }

    [Fact]
    public void Updating_steps_uses_a_deep_expected_snapshot_and_updates_timestamp()
    {
        var repository = new FakeTodoRepository();
        var createdAt = DateTimeOffset.Parse("2026-09-15T08:00:00Z");
        var updatedAt = DateTimeOffset.Parse("2026-09-15T08:05:00Z");
        var service = new TodoApplicationService(repository, new FixedTimeProvider(createdAt));
        var original = service.Create("Ship", null, TodoPriority.Normal, null, new[] { "Prepare", "Check" });
        var expected = CloneTodo(original);
        var replacementSteps = new[]
        {
            new TodoStep(original.Steps[0].Id, "Prepare build", 0, true),
            new TodoStep(original.Steps[1].Id, "Check release", 1),
        };

        var updated = new TodoApplicationService(repository, new FixedTimeProvider(updatedAt))
            .UpdateSteps(expected, replacementSteps);

        Assert.Equal(updatedAt, updated.UpdatedAt);
        Assert.Equal(replacementSteps, updated.Steps);
        Assert.Equal(updated, repository.Saved);
        Assert.NotSame(original, expected);
        Assert.NotSame(original.Steps, expected.Steps);
    }

    [Fact]
    public void Updating_steps_rejects_a_stale_deep_snapshot_without_writing()
    {
        var repository = new FakeTodoRepository();
        var service = new TodoApplicationService(repository, TimeProvider.System);
        var original = service.Create("Ship", null, TodoPriority.Normal, null, new[] { "Prepare", "Check" });
        var expected = CloneTodo(original);
        var current = new TodoItem(original.Id, original.Title, original.Description, original.Priority, original.DueAt,
            original.CreatedAt, original.UpdatedAt.AddSeconds(1), original.Status, original.CompletedAt,
            new[]
            {
                new TodoStep(original.Steps[0].Id, "Changed elsewhere", 0),
                original.Steps[1],
            });
        repository.Saved = current;

        var error = Assert.Throws<InvalidOperationException>(() => service.UpdateSteps(expected, expected.Steps));

        Assert.Contains("任务已变化", error.Message);
        Assert.Equal(current, repository.Saved);
        Assert.Equal(1, repository.SaveCount);
    }

    [Fact]
    public void Updating_steps_by_id_reuses_protection_and_timestamp_rules()
    {
        var repository = new FakeTodoRepository();
        var at = DateTimeOffset.Parse("2026-09-15T08:10:00Z");
        var service = new TodoApplicationService(repository, new FixedTimeProvider(at));
        var original = service.Create("Ship", null, TodoPriority.Normal, null, new[] { "Prepare" });
        var updated = service.UpdateSteps(original.Id,
            new[] { new TodoStep(original.Steps[0].Id, "Prepared", 0, true) });

        Assert.Equal(at, updated.UpdatedAt);
        Assert.True(updated.Steps[0].IsCompleted);
    }

    [Fact]
    public void Failed_conditional_step_update_does_not_raise_changed_notification()
    {
        var repository = new FakeTodoRepository { RejectUpdates = true };
        var service = new TodoApplicationService(repository, TimeProvider.System);
        var original = service.Create("Ship", null, TodoPriority.Normal, null, new[] { "Prepare" });
        var notifications = 0;
        service.Changed += () => notifications++;

        Assert.Throws<InvalidOperationException>(() => service.UpdateSteps(original,
            new[] { new TodoStep(original.Steps[0].Id, "Prepared", 0) }));

        Assert.Equal(0, notifications);
        Assert.Equal(original, repository.Saved);
    }

    [Fact]
    public void Completed_todo_rejects_all_content_edits_but_can_reopen_and_delete()
    {
        var repository = new FakeTodoRepository();
        var time = DateTimeOffset.Parse("2026-09-15T08:20:00Z");
        var service = new TodoApplicationService(repository, new FixedTimeProvider(time));
        var planned = service.Create("Ship", "Keep", TodoPriority.Normal, null, new[] { "Prepare" });
        var completed = service.Complete(planned.Id, confirmIncompleteSteps: true);

        Assert.Throws<InvalidOperationException>(() => service.Update(completed.Id, "Changed", "Changed"));
        Assert.Throws<InvalidOperationException>(() => service.Update(completed.Id, "Changed", "Changed", completed.Steps));
        Assert.Throws<InvalidOperationException>(() => service.UpdateSteps(completed, completed.Steps));
        Assert.Equal(completed, repository.Saved);

        var reopened = service.Reopen(completed.Id);
        var edited = service.Update(reopened.Id, "Changed", "Changed");
        Assert.Equal("Changed", edited.Title);
        service.Delete(edited.Id);
        Assert.Null(repository.Saved);
    }

    [Fact]
    public void Active_and_unknown_execution_reject_step_updates_with_review_message()
    {
        var repository = new FakeTodoRepository();
        var service = new TodoApplicationService(repository, TimeProvider.System);
        var planned = service.Create("Ship", null, TodoPriority.Normal, null, new[] { "Prepare" });
        var active = planned.Activate(DateTimeOffset.UtcNow);
        repository.Saved = active;

        var activeError = Assert.Throws<InvalidOperationException>(() => service.UpdateSteps(active,
            active.Steps));
        Assert.Contains("核对", activeError.Message);
        Assert.DoesNotContain("兼容记录", activeError.Message);

        var path = CreateTemporaryDatabasePath();
        try
        {
            var database = TestRuntimeDatabase.Create(path);
            new RuntimeDatabaseMigrator(database).Migrate();
            var agents = new SqliteAgentRepository(database);
            agents.SaveExecution(new FgoPet.Core.Agents.AgentExecution(
                "execution-unknown", planned.Id, "codex", "instance-1", "task-1", "request-1",
                DateTimeOffset.UtcNow, FgoPet.Core.Agents.AgentExecutionStatus.DispatchOutcomeUnknown));
            repository.Saved = planned;
            var protectedService = new TodoApplicationService(repository, TimeProvider.System, agents);

            var unknownError = Assert.Throws<InvalidOperationException>(() => protectedService.UpdateSteps(planned,
                planned.Steps));
            Assert.Contains("核对", unknownError.Message);
            Assert.DoesNotContain("兼容记录", unknownError.Message);
            Assert.Equal(planned, repository.Saved);
        }
        finally
        {
            DeleteTemporaryDatabase(path);
        }
    }

    [Fact]
    public void Completing_with_incomplete_steps_requires_explicit_confirmation()
    {
        var repository = new FakeTodoRepository();
        var service = new TodoApplicationService(repository, TimeProvider.System);
        var todo = service.Create("Ship", null, TodoPriority.Normal, null, new[] { "Prepare", "Check" });

        var error = Assert.Throws<InvalidOperationException>(() => service.Complete(todo.Id));
        Assert.Contains("2", error.Message);
        Assert.Equal(TodoStatus.Planned, repository.Saved!.Status);

        var completed = service.Complete(todo.Id, confirmIncompleteSteps: true);
        Assert.Equal(TodoStatus.Completed, completed.Status);
        Assert.Equal(2, completed.Steps.Count);
    }

    [Fact]
    public void Deleting_an_active_todo_is_rejected_by_the_application_service()
    {
        var repository = new FakeTodoRepository();
        var service = new TodoApplicationService(repository, TimeProvider.System);
        var todo = service.Create("Running", null, TodoPriority.Normal, null).Activate(DateTimeOffset.UtcNow);
        repository.Saved = todo;

        Assert.Throws<InvalidOperationException>(() => service.Delete(todo.Id));
        Assert.Null(repository.DeletedId);
    }

    [Fact]
    public void Editing_preserves_existing_priority_due_date_and_identity()
    {
        var repository = new FakeTodoRepository();
        var service = new TodoApplicationService(repository, TimeProvider.System);
        var due = DateTimeOffset.UtcNow.AddDays(2);
        var original = service.Create("Before", "Steps", TodoPriority.High, due);
        var edited = service.Update(original.Id, "After", "1. Learn\n2. Explain");
        Assert.Equal(original.Id, edited.Id);
        Assert.Equal(original.Priority, edited.Priority);
        Assert.Equal(due, edited.DueAt);
        Assert.Equal(original.CreatedAt, edited.CreatedAt);
    }

    [Fact]
    public void Manual_completion_can_be_undone_but_active_execution_cannot_be_completed()
    {
        var repository = new FakeTodoRepository();
        var service = new TodoApplicationService(repository, TimeProvider.System);
        var todo = service.Create("Learn", null, TodoPriority.Normal, null);
        var completed = service.Complete(todo.Id);
        Assert.Equal(TodoStatus.Completed, repository.Saved!.Status);
        service.UndoCompletion(completed);
        Assert.Equal(TodoStatus.Planned, repository.Saved!.Status);
        repository.Saved = repository.Saved.Activate(DateTimeOffset.UtcNow);
        Assert.Throws<InvalidOperationException>(() => service.Complete(todo.Id));
        Assert.Throws<InvalidOperationException>(() => service.Update(todo.Id, "Changed", null));
        Assert.Equal(TodoStatus.Active, repository.Saved.Status);
    }

    [Fact]
    public void Completed_todo_can_be_reopened_after_the_service_is_recreated_without_losing_fields()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fgo-todo-service-{Guid.NewGuid():N}.db");
        try
        {
            var database = TestRuntimeDatabase.Create(path);
            new RuntimeDatabaseMigrator(database).Migrate();
            var repository = new SqliteTodoRepository(database);
            var firstTime = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-13T01:02:03Z"));
            var service = new TodoApplicationService(repository, firstTime);
            var due = DateTimeOffset.Parse("2026-09-20T08:00:00+08:00");
            var original = service.Create("Remember this", "Keep every field", TodoPriority.High, due);
            var completed = service.Complete(original.Id);
            var agents = new SqliteAgentRepository(database);
            var execution = new FgoPet.Core.Agents.AgentExecution("execution-terminal", completed.Id, "codex", "instance-1", "task-1", "request-1",
                completed.UpdatedAt, FgoPet.Core.Agents.AgentExecutionStatus.Completed, endedAt: completed.UpdatedAt);
            agents.SaveExecution(execution);

            var reopenedAt = DateTimeOffset.Parse("2026-09-13T01:10:03Z");
            var recreated = new TodoApplicationService(new SqliteTodoRepository(database), new FixedTimeProvider(reopenedAt), agents);
            var reopened = recreated.Reopen(completed.Id);

            Assert.Equal(TodoStatus.Planned, reopened.Status);
            Assert.Null(reopened.CompletedAt);
            Assert.Equal(reopenedAt, reopened.UpdatedAt);
            Assert.Equal(original.Id, reopened.Id);
            Assert.Equal(original.Title, reopened.Title);
            Assert.Equal(original.Description, reopened.Description);
            Assert.Equal(original.Priority, reopened.Priority);
            Assert.Equal(original.DueAt, reopened.DueAt);
            Assert.Equal(original.CreatedAt, reopened.CreatedAt);
            Assert.Equal(reopened, repository.Get(reopened.Id));
            Assert.Equal(execution, agents.GetExecution(execution.Id));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                var file = path + suffix;
                if (File.Exists(file)) File.Delete(file);
            }
        }
    }

    [Fact]
    public void Reopen_rejects_non_completed_and_agent_protected_todos()
    {
        var repository = new FakeTodoRepository();
        var service = new TodoApplicationService(repository, TimeProvider.System);
        var planned = service.Create("Planned", null, TodoPriority.Normal, null);
        Assert.Throws<InvalidOperationException>(() => service.Reopen(planned.Id));

        var active = planned.Activate(DateTimeOffset.UtcNow);
        repository.Saved = active;
        Assert.Throws<InvalidOperationException>(() => service.Reopen(active.Id));

        var completed = active.ReturnToPlanned(DateTimeOffset.UtcNow).Complete(DateTimeOffset.UtcNow);
        repository.Saved = completed;
        var path = Path.Combine(Path.GetTempPath(), $"fgo-todo-service-{Guid.NewGuid():N}.db");
        try
        {
            var database = TestRuntimeDatabase.Create(path);
            new RuntimeDatabaseMigrator(database).Migrate();
            var agents = new SqliteAgentRepository(database);
            agents.SaveExecution(new FgoPet.Core.Agents.AgentExecution("execution-1", completed.Id, "codex", "instance-1", "task-1", "request-1",
                DateTimeOffset.UtcNow, FgoPet.Core.Agents.AgentExecutionStatus.DispatchOutcomeUnknown));
            var protectedService = new TodoApplicationService(repository, TimeProvider.System, agents);

            Assert.Throws<InvalidOperationException>(() => protectedService.Reopen(completed.Id));
            Assert.Equal(completed, repository.Saved);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                var file = path + suffix;
                if (File.Exists(file)) File.Delete(file);
            }
        }
    }

    [Fact]
    public void Undo_completion_rejects_its_old_snapshot_after_the_completed_todo_is_modified()
    {
        var repository = new FakeTodoRepository();
        var service = new TodoApplicationService(repository, TimeProvider.System);
        var completed = service.Complete(service.Create("Before", "Original", TodoPriority.Normal, null).Id);
        var edited = new TodoItem(completed.Id, "After", "Changed", completed.Priority, completed.DueAt,
            completed.CreatedAt, completed.UpdatedAt.AddSeconds(1), TodoStatus.Completed, completed.CompletedAt);
        repository.ConcurrentReplacement = edited;

        Assert.Throws<InvalidOperationException>(() => service.UndoCompletion(completed));
        Assert.Equal(edited, repository.Saved);
        Assert.Equal(TodoStatus.Completed, repository.Saved!.Status);
    }

    [Fact]
    public void Reopen_does_not_overwrite_a_concurrent_local_update_when_compare_and_swap_fails()
    {
        var repository = new FakeTodoRepository();
        var service = new TodoApplicationService(repository, TimeProvider.System);
        var completed = service.Complete(service.Create("Before", null, TodoPriority.Normal, null).Id);
        var concurrent = new TodoItem(completed.Id, "Concurrent edit", completed.Description, completed.Priority, completed.DueAt,
            completed.CreatedAt, completed.UpdatedAt.AddSeconds(1), TodoStatus.Completed, completed.CompletedAt);
        repository.ConcurrentReplacement = concurrent;

        Assert.Throws<InvalidOperationException>(() => service.Reopen(completed.Id));
        Assert.Equal(concurrent, repository.Saved);
    }

    private static TodoItem CloneTodo(TodoItem source) => new(
        source.Id, source.Title, source.Description, source.Priority, source.DueAt,
        source.CreatedAt, source.UpdatedAt, source.Status, source.CompletedAt,
        source.Steps.Select(step => new TodoStep(step.Id, step.Title, step.Order, step.IsCompleted)).ToArray());

    private static string CreateTemporaryDatabasePath() =>
        Path.Combine(Path.GetTempPath(), $"fgo-todo-service-{Guid.NewGuid():N}.db");

    private static void DeleteTemporaryDatabase(string path)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var file = path + suffix;
            if (File.Exists(file)) File.Delete(file);
        }
    }

    private sealed class FakeTodoRepository : ITodoRepository
    {
        public int SaveCount { get; private set; }
        public TodoItem? Saved { get; set; }
        public string? DeletedId { get; private set; }
        public TodoItem? ConcurrentReplacement { get; set; }
        public bool RejectUpdates { get; set; }
        public void Save(TodoItem todo) { Saved = todo; SaveCount++; }
        public bool TryUpdateLocal(TodoItem expected, TodoItem? replacement)
        {
            if (RejectUpdates) return false;
            if (ConcurrentReplacement is not null)
            {
                Saved = ConcurrentReplacement;
                ConcurrentReplacement = null;
                return false;
            }

            if (Get(expected.Id) != expected || expected.Status == TodoStatus.Active) return false;
            if (replacement is null) Delete(expected.Id); else Save(replacement);
            return true;
        }
        public TodoItem? Get(string id) => Saved?.Id == id ? Saved : null;
        public IReadOnlyList<TodoItem> List(TodoStatus? status = null) => Saved is null || status is not null && Saved.Status != status ? Array.Empty<TodoItem>() : new[] { Saved };
        public IReadOnlyList<TodoItem> ListCompletedOn(DateOnly localDate) => Saved?.CompletedAt?.ToLocalTime().Date == localDate.ToDateTime(TimeOnly.MinValue).Date ? new[] { Saved } : Array.Empty<TodoItem>();
        public void Delete(string id) { DeletedId = id; Saved = null; }
        public void ClearAgentTodoData() => Saved = null;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
