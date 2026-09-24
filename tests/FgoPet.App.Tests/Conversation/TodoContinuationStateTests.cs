using System.IO;
using FgoPet.App.Dialogue;
using FgoPet.App.Services;
using FgoPet.Core.Todo;
using Xunit;

namespace FgoPet.App.Tests.Conversation;

public sealed class TodoContinuationStateTests
{
    [Fact]
    public void Replace_keeps_one_session_draft_and_increments_version()
    {
        var repository = new FakeTodoRepository();
        var state = CreateState(repository);
        var first = state.Replace("conversation", "servant", [new TodoProposal("first")]);
        var second = state.Replace("conversation", "servant", [new TodoProposal("second", stepTitles: ["check"])]);

        Assert.Equal(first.DraftId, second.DraftId);
        Assert.Equal(2, second.Version);
        Assert.Equal("second", Assert.Single(second.Proposals).Title);
        Assert.Empty(repository.Items);
    }

    [Fact]
    public void Confirm_is_idempotent_and_writes_steps_only_once()
    {
        var repository = new FakeTodoRepository();
        var state = CreateState(repository);
        var draft = state.Replace("conversation", "servant", [new TodoProposal("ship", stepTitles: ["prepare", "check"])]);

        var first = state.Confirm("conversation", "servant", draft.DraftId, draft.Version, "confirm-1");
        var second = state.Confirm("conversation", "servant", draft.DraftId, draft.Version, "confirm-1");

        Assert.Equal(TodoDraftResultKind.Committed, first.Kind);
        Assert.Equal(TodoDraftResultKind.AlreadyCommitted, second.Kind);
        Assert.Single(repository.Items);
        Assert.Equal(2, repository.Items[0].Steps.Count);
    }

    [Fact]
    public void Confirm_exposes_every_created_todo_for_a_multi_goal_draft()
    {
        var repository = new FakeTodoRepository();
        var state = CreateState(repository);
        var draft = state.Replace("conversation", "servant", [
            new TodoProposal("first"),
            new TodoProposal("second", stepTitles: ["check"]),
        ]);

        var result = state.Confirm("conversation", "servant", draft.DraftId, draft.Version, "confirm-multi");

        Assert.Equal(TodoDraftResultKind.Committed, result.Kind);
        Assert.Equal(2, result.Todos.Count);
        Assert.Equal(["first", "second"], result.Todos.Select(todo => todo.Title).ToArray());
        Assert.Equal(result.Todos[0].Id, result.Todo?.Id);
        Assert.Equal(2, repository.Items.Count);
    }

    [Fact]
    public void Confirm_retry_after_partial_failure_reuses_the_same_todo_ids()
    {
        var repository = new FakeTodoRepository { ThrowOnSaveNumber = 3 };
        var state = CreateState(repository);
        var draft = state.Replace("conversation", "servant", [
            new TodoProposal("first"),
            new TodoProposal("second"),
            new TodoProposal("third"),
        ]);

        var failed = state.Confirm("conversation", "servant", draft.DraftId, draft.Version, "confirm-partial");
        repository.ThrowOnSaveNumber = null;
        var retried = state.Confirm("conversation", "servant", draft.DraftId, draft.Version, "confirm-partial");

        Assert.Equal(TodoDraftResultKind.Unknown, failed.Kind);
        Assert.Equal(TodoDraftResultKind.Committed, retried.Kind);
        Assert.Equal(3, retried.Todos.Count);
        Assert.Equal(3, repository.Items.Count);
        Assert.Equal(3, repository.Items.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Stale_confirmation_and_cancel_do_not_write()
    {
        var repository = new FakeTodoRepository();
        var state = CreateState(repository);
        var draft = state.Replace("conversation", "servant", [new TodoProposal("ship")]);
        state.Replace("conversation", "servant", [new TodoProposal("revised")]);

        var stale = state.Confirm("conversation", "servant", draft.DraftId, draft.Version, "confirm-stale");
        var cancelled = state.Cancel("conversation", "servant");

        Assert.Equal(TodoDraftResultKind.Stale, stale.Kind);
        Assert.Equal(TodoDraftResultKind.Cancelled, cancelled.Kind);
        Assert.Empty(repository.Items);
    }

    private static TodoContinuationState CreateState(FakeTodoRepository repository) =>
        new(new TodoProposalService(new TodoApplicationService(repository, TimeProvider.System)));

    [Fact]
    public async Task Concurrent_confirmations_commit_once_and_all_retries_return_the_same_todos()
    {
        var repository = new FakeTodoRepository();
        var state = CreateState(repository);
        var draft = state.Replace("conversation", "servant", [new TodoProposal("first"), new TodoProposal("second")]);
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
            state.Confirm("conversation", "servant", draft.DraftId, draft.Version, "same-request"))));

        Assert.Single(results, result => result.Kind == TodoDraftResultKind.Committed);
        Assert.Equal(15, results.Count(result => result.Kind == TodoDraftResultKind.AlreadyCommitted));
        Assert.All(results, result => Assert.Equal(["first", "second"], result.Todos.Select(todo => todo.Title).ToArray()));
        Assert.Equal(2, repository.SaveCount);
        Assert.Equal(2, repository.Items.Count);
    }

    [Fact]
    public void Reusing_a_retry_key_in_another_context_never_returns_the_first_contexts_todos()
    {
        var repository = new FakeTodoRepository();
        var state = CreateState(repository);
        var first = state.Replace("a", "role-a", [new TodoProposal("first")]);
        state.Confirm("a", "role-a", first.DraftId, first.Version, "shared-key");
        var second = state.Replace("b", "role-b", [new TodoProposal("second")]);

        var result = state.Confirm("b", "role-b", second.DraftId, second.Version, "shared-key");

        Assert.Equal(TodoDraftResultKind.Committed, result.Kind);
        Assert.Equal("second", Assert.Single(result.Todos).Title);
        Assert.Equal(2, repository.Items.Count);
        Assert.Equal(TodoDraftResultKind.Stale,
            state.Confirm("a", "role-a", first.DraftId, first.Version + 1, "shared-key").Kind);
    }

    [Fact]
    public void Pending_draft_contents_cannot_change_without_replacement_and_a_new_version()
    {
        var state = CreateState(new FakeTodoRepository());
        var proposals = new[] { new TodoProposal("original", stepTitles: ["original step"]) };
        var draft = state.Replace("conversation", "servant", proposals);
        proposals[0] = new TodoProposal("different");

        Assert.Equal("original", draft.Proposals[0].Title);
        Assert.Throws<NotSupportedException>(() => ((IList<TodoProposal>)draft.Proposals)[0] = new TodoProposal("mutated"));
        Assert.Throws<NotSupportedException>(() => ((IList<string>)draft.Proposals[0].StepTitles)[0] = "mutated");
    }

    private sealed class FakeTodoRepository : ITodoRepository
    {
        public List<TodoItem> Items { get; } = [];
        public int? ThrowOnSaveNumber { get; set; }
        private int _saveCount;
        public int SaveCount => _saveCount;
        public void Save(TodoItem todo)
        {
            _saveCount++;
            if (ThrowOnSaveNumber == _saveCount) throw new IOException("simulated partial write");
            Items.RemoveAll(item => item.Id == todo.Id);
            Items.Add(todo);
        }
        public TodoItem? Get(string id) => Items.SingleOrDefault(item => item.Id == id);
        public IReadOnlyList<TodoItem> List(TodoStatus? status = null) => status is null ? Items.ToArray() : Items.Where(item => item.Status == status).ToArray();
        public IReadOnlyList<TodoItem> ListCompletedOn(DateOnly localDate) => [];
        public void Delete(string id) => Items.RemoveAll(item => item.Id == id);
        public void ClearAgentTodoData() => Items.Clear();
        public bool TryUpdateLocal(TodoItem expected, TodoItem? replacement)
        {
            var current = Get(expected.Id);
            if (current is null) return false;
            if (replacement is null) Delete(expected.Id); else Save(replacement);
            return true;
        }
    }
}
