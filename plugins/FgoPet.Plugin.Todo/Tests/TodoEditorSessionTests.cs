using FgoPet.Core.Todo;
using Xunit;

namespace FgoPet.Plugin.Todo.Tests;

public sealed class TodoEditorSessionTests
{
    [Fact]
    public void Refresh_preserves_the_draft_and_original_snapshot_for_conflict_detection()
    {
        var original = Item("original");
        var editor = new TodoEditorSession(original, true, false);
        editor.BeginEditing();
        editor.DraftTitle = "unsaved draft";
        editor.Update(Item("changed elsewhere"), true, false);
        Assert.Equal("unsaved draft", editor.DraftTitle);
        Assert.Same(original, editor.EditOriginal);
        Assert.Equal("changed elsewhere", editor.Item.Title);
        editor.EndEditing();
        Assert.False(editor.IsEditing);
        Assert.Null(editor.EditOriginal);
        editor.BeginEditing();
        Assert.Equal("changed elsewhere", editor.DraftTitle);
    }

    [Fact]
    public void New_step_identity_survives_retry_but_is_released_on_cancel()
    {
        var editor = new TodoEditorSession(Item(), true, false);
        editor.BeginAddingStep();
        editor.NewStepDraft = "step draft";
        editor.AssignNewStepIdIfNeeded();
        var id = editor.NewStepId;
        editor.AssignNewStepIdIfNeeded();
        Assert.Equal(id, editor.NewStepId);
        Assert.True(editor.CanSaveNewStep);
        editor.EndAddingStep();
        Assert.Null(editor.NewStepId);
        Assert.Null(editor.NewStepOriginal);
        Assert.Empty(editor.Item.Steps);
    }

    [Fact]
    public void Step_refresh_preserves_an_active_edit_and_completion_confirmation_keeps_its_snapshot()
    {
        var original = Item(steps: [new("step-a", "original step", 0)]);
        var editor = new TodoEditorSession(original, true, false);
        var step = Assert.Single(editor.Steps.Cast<TodoStepEditor>());
        step.BeginEditing();
        step.DraftTitle = "unsaved step";
        editor.Update(Item(steps: [new("step-a", "changed step", 0)]), true, false);
        Assert.Same(step, Assert.Single(editor.Steps.Cast<TodoStepEditor>()));
        Assert.Equal("unsaved step", step.DraftTitle);
        Assert.Same(original, step.EditOriginal);
        step.EndEditing();
        Assert.Equal("changed step", step.DraftTitle);
        editor.BeginCompletionConfirmation();
        var expected = editor.CompletionOriginal;
        editor.Update(Item("changed task"), true, false);
        Assert.Same(expected, editor.CompletionOriginal);
        Assert.False(editor.CanEditSteps);
        editor.EndCompletionConfirmation();
        Assert.Null(editor.CompletionOriginal);
    }

    [Fact]
    public void Reordered_and_removed_steps_retain_only_matching_identities()
    {
        var editor = new TodoEditorSession(Item(steps: [new("a", "A", 0), new("b", "B", 1)]), true, false);
        var rows = editor.Steps.Cast<TodoStepEditor>().ToArray();
        editor.Update(Item(steps: [new("b", "B", 0), new("c", "C", 1)]), true, false);
        var refreshed = editor.Steps.Cast<TodoStepEditor>().ToArray();
        Assert.Same(rows[1], refreshed[0]);
        Assert.DoesNotContain(rows[0], refreshed);
        Assert.Equal(-1, editor.StepIndex("missing"));
    }

    private static TodoItem Item(string title = "task", IReadOnlyList<TodoStep>? steps = null) =>
        new("todo-1", title, null, TodoPriority.Normal, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, steps: steps);
}
