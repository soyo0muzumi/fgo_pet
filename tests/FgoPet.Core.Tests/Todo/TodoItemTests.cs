using FgoPet.Core.Todo;
using Xunit;

namespace FgoPet.Core.Tests.Todo;

public sealed class TodoItemTests
{
    private static readonly DateTimeOffset CreatedAt = DateTimeOffset.Parse("2026-08-30T08:00:00Z");

    [Fact]
    public void Todo_moves_from_planned_to_active_to_completed()
    {
        var todo = new TodoItem(
            "todo-1",
            "Prepare release notes",
            "Summarize the accepted changes.",
            TodoPriority.High,
            DateTimeOffset.Parse("2026-08-31T00:00:00Z"),
            CreatedAt,
            CreatedAt);

        var active = todo.Activate(CreatedAt.AddMinutes(1));
        var completed = active.Complete(CreatedAt.AddMinutes(2));

        Assert.Equal(TodoStatus.Active, active.Status);
        Assert.Equal(TodoStatus.Completed, completed.Status);
        Assert.Equal(CreatedAt.AddMinutes(2), completed.CompletedAt);
        Assert.False(completed.CanDispatch);
    }

    [Fact]
    public void Completed_todo_cannot_be_activated_or_replanned()
    {
        var completed = new TodoItem("todo-1", "Done", null, TodoPriority.Normal, null, CreatedAt, CreatedAt)
            .Activate(CreatedAt.AddMinutes(1))
            .Complete(CreatedAt.AddMinutes(2));

        Assert.Throws<InvalidOperationException>(() => completed.Activate(CreatedAt.AddMinutes(3)));
        Assert.Throws<InvalidOperationException>(() => completed.ReturnToPlanned(CreatedAt.AddMinutes(3)));
    }

    [Fact]
    public void Failed_or_cancelled_execution_can_return_active_todo_to_planned()
    {
        var active = new TodoItem("todo-1", "Retry me", null, TodoPriority.Normal, null, CreatedAt, CreatedAt)
            .Activate(CreatedAt.AddMinutes(1));

        var planned = active.ReturnToPlanned(CreatedAt.AddMinutes(2));

        Assert.Equal(TodoStatus.Planned, planned.Status);
        Assert.Null(planned.CompletedAt);
        Assert.True(planned.CanDispatch);
    }
}
