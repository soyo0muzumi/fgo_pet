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

    [Fact]
    public void Todo_copies_steps_and_preserves_them_when_parent_fields_change()
    {
        var source = new List<TodoStep>
        {
            new("step-1", "Prepare", 0),
            new("step-2", "Check", 1, isCompleted: true),
        };

        var todo = new TodoItem("todo-1", "Release", null, TodoPriority.Normal, null, CreatedAt, CreatedAt, steps: source);
        source[0] = new TodoStep("step-3", "Changed outside snapshot", 0);

        var updated = todo.WithSteps(new[] { new TodoStep("step-1", "Prepare", 0, isCompleted: true) });

        Assert.Equal("Prepare", todo.Steps[0].Title);
        Assert.Equal(2, todo.Steps.Count);
        Assert.Single(updated.Steps);
        Assert.Equal(TodoStatus.Planned, updated.Status);
        Assert.True(updated.Steps[0].IsCompleted);
    }

    [Fact]
    public void Todo_rejects_duplicate_or_non_contiguous_step_ids_and_orders()
    {
        var duplicate = new[]
        {
            new TodoStep("step-1", "Prepare", 0),
            new TodoStep("step-1", "Check", 1),
        };
        var gap = new[] { new TodoStep("step-1", "Prepare", 1) };

        Assert.Throws<ArgumentException>(() =>
            new TodoItem("todo-1", "Release", null, TodoPriority.Normal, null, CreatedAt, CreatedAt, steps: duplicate));
        Assert.Throws<ArgumentException>(() =>
            new TodoItem("todo-1", "Release", null, TodoPriority.Normal, null, CreatedAt, CreatedAt, steps: gap));
    }

    [Fact]
    public void Todo_accepts_at_most_twenty_steps()
    {
        var twenty = Enumerable.Range(0, 20)
            .Select(index => new TodoStep($"step-{index}", $"Step {index}", index))
            .ToArray();
        var twentyOne = twenty.Append(new TodoStep("step-20", "Step 20", 0)).ToArray();

        var todo = new TodoItem("todo-1", "Release", null, TodoPriority.Normal, null, CreatedAt, CreatedAt, steps: twenty);

        Assert.Equal(20, todo.Steps.Count);
        Assert.Throws<ArgumentException>(() =>
            new TodoItem("todo-1", "Release", null, TodoPriority.Normal, null, CreatedAt, CreatedAt, steps: twentyOne));
    }

    [Fact]
    public void Completing_all_steps_does_not_complete_the_parent_todo()
    {
        var todo = new TodoItem(
            "todo-1", "Release", null, TodoPriority.Normal, null, CreatedAt, CreatedAt,
            steps: new[]
            {
                new TodoStep("step-1", "Prepare", 0, isCompleted: true),
                new TodoStep("step-2", "Check", 1, isCompleted: true),
            });

        Assert.Equal(TodoStatus.Planned, todo.Status);
        Assert.Null(todo.CompletedAt);
    }

    [Fact]
    public void Todo_value_comparer_compares_step_values_in_order()
    {
        var left = new TodoItem("todo-1", "Release", null, TodoPriority.Normal, null, CreatedAt, CreatedAt,
            steps: new[] { new TodoStep("step-1", "Prepare", 0) });
        var sameValues = new TodoItem("todo-1", "Release", null, TodoPriority.Normal, null, CreatedAt, CreatedAt,
            steps: new[] { new TodoStep("step-1", "Prepare", 0) });
        var changed = sameValues.WithSteps(new[] { new TodoStep("step-1", "Check", 0) });

        Assert.True(TodoItemValueComparer.Equals(left, sameValues));
        Assert.False(TodoItemValueComparer.Equals(left, changed));
    }
}
