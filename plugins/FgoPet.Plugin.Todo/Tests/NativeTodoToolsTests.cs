using System.Text.Json;
using FgoPet.App.Dialogue;
using FgoPet.App.Services;
using FgoPet.Core.Todo;
using FgoPet.Extensibility;
using FgoPet.Plugin.Todo;
using Xunit;

namespace FgoPet.Plugin.Todo.Tests;

public sealed class NativeTodoToolsTests
{
    private static readonly ToolScope Scope = new("conversation", "role", null);
    private static ToolInvocation Invocation(object args) => new(Scope, JsonSerializer.SerializeToElement(args)) { ExecutionContext = new("run", 1, "call", new string('A', 64)) };
    private static IReadOnlyList<IToolProvider> Tools(Repository repo)
    {
        var application = new TodoApplicationService(repo, TimeProvider.System);
        return NativeTodoTools.Create(application, repo, new TodoProposalService(application), TimeProvider.System);
    }
    [Fact]
    public void Legacy_confirmation_cannot_consume_a_native_approval_draft()
    {
        var repo = new Repository(); var application = new TodoApplicationService(repo, TimeProvider.System);
        var proposals = new TodoProposalService(application);
        var tool = NativeTodoTools.Create(application, repo, proposals, TimeProvider.System).Single(t => t.Descriptor.Name == "todo.create");
        ((IToolBusinessConfirmationProvider)tool).PrepareConfirmation(Invocation(new { title = "fixture" }), default);
        var pending = proposals.Drafts.Get(Scope.ConversationId, Scope.RoleId)!;
        Assert.True(pending.RequiresNativeConfirmation);
        Assert.Null(new TodoPlugin(proposals).TryHandleInput(Scope, "确认创建"));
        Assert.Equal(TodoDraftResultKind.Stale, proposals.Drafts.Confirm(Scope.ConversationId, Scope.RoleId, pending.DraftId, pending.Version, "legacy-confirm").Kind);
        Assert.Empty(repo.Items);
    }
    [Fact]
    public async Task Create_freezes_existing_owner_draft_and_requires_exact_host_confirmation()
    {
        var repo = new Repository(); var tool = Tools(repo).Single(t => t.Descriptor.Name == "todo.create");
        var invocation = Invocation(new { title = "fixture", steps = new[] { new { title = "first" } } });
        var unconfirmed = await tool.InvokeAsync(invocation, default);
        Assert.Equal("TODO_CONFIRMATION_REQUIRED", unconfirmed.ErrorCode); Assert.Empty(repo.Items);
        var owner = (IToolBusinessConfirmationProvider)tool;
        var draft = owner.PrepareConfirmation(invocation, default);
        Assert.Empty(repo.Items);
        var confirmed = invocation with { ExecutionContext = invocation.ExecutionContext! with { BusinessConfirmation = draft } };
        var result = await tool.InvokeAsync(confirmed, default);
        Assert.True(result.Success); Assert.Equal(ToolExecutionState.Committed, result.ExecutionState);
        Assert.Single(repo.Items); Assert.Single(repo.Items[0].Steps);
        result = await tool.InvokeAsync(confirmed, default);
        Assert.True(result.Success); Assert.Single(repo.Items); Assert.Equal(1, repo.Writes);
    }
    [Fact]
    public async Task Update_and_complete_reject_stale_versions_and_never_overwrite_external_edit()
    {
        var repo = new Repository(); var at = DateTimeOffset.UnixEpoch;
        var item = new TodoItem("fixture", "before", null, TodoPriority.Normal, null, at, at); repo.Save(item);
        var tools = Tools(repo); var update = tools.Single(t => t.Descriptor.Name == "todo.update");
        var invocation = Invocation(new { id = item.Id, expectedVersion = TodoItemVersion.Of(item), title = "after" });
        var owner = (IToolBusinessConfirmationProvider)update; var draft = owner.PrepareConfirmation(invocation, default);
        repo.Save(new TodoItem(item.Id, "external", null, item.Priority, null, at, at.AddSeconds(1)));
        var denied = await update.InvokeAsync(invocation with { ExecutionContext = invocation.ExecutionContext! with { BusinessConfirmation = draft } }, default);
        Assert.False(denied.Success); Assert.Equal(ToolExecutionState.NotExecuted, denied.ExecutionState);
        Assert.Equal("external", repo.Get(item.Id)!.Title);
        var complete = tools.Single(t => t.Descriptor.Name == "todo.complete");
        Assert.Throws<ToolBusinessConfirmationException>(() => ((IToolBusinessConfirmationProvider)complete).PrepareConfirmation(
            Invocation(new { id = item.Id, expectedVersion = TodoItemVersion.Of(item) }), default));
    }
    [Fact]
    public async Task Complete_preserves_checklist_confirmation_and_reports_real_item_version()
    {
        var repo = new Repository(); var at = DateTimeOffset.UnixEpoch;
        var item = new TodoItem("fixture", "before", null, TodoPriority.Normal, null, at, at, steps: [new("step", "pending", 0)]); repo.Save(item);
        var complete = Tools(repo).Single(t => t.Descriptor.Name == "todo.complete");
        var invocation = Invocation(new { id = item.Id, expectedVersion = TodoItemVersion.Of(item), confirmIncompleteSteps = true });
        var draft = ((IToolBusinessConfirmationProvider)complete).PrepareConfirmation(invocation, default);
        var result = await complete.InvokeAsync(invocation with { ExecutionContext = invocation.ExecutionContext! with { BusinessConfirmation = draft } }, default);
        Assert.True(result.Success); Assert.Equal(TodoStatus.Completed, repo.Get(item.Id)!.Status);
        Assert.Equal(TodoItemVersion.Of(repo.Get(item.Id)!), result.Payload.GetProperty("version").GetString());
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Unknown_result_reconciles_only_a_receipt_without_repeating_the_command(bool committed)
    {
        var repo = new Repository { ThrowAfterCommit = committed, ThrowBeforeCommit = !committed };
        var tool = Tools(repo).Single(t => t.Descriptor.Name == "todo.create");
        var invocation = Invocation(new { title = "fixture" });
        var draft = ((IToolBusinessConfirmationProvider)tool).PrepareConfirmation(invocation, default);
        var confirmed = invocation with { ExecutionContext = invocation.ExecutionContext! with { BusinessConfirmation = draft } };
        var first = await tool.InvokeAsync(confirmed, default);
        var repeated = await tool.InvokeAsync(confirmed, default);
        Assert.Equal(ToolExecutionState.Unknown, first.ExecutionState);
        Assert.Equal(committed ? ToolExecutionState.Committed : ToolExecutionState.Unknown, repeated.ExecutionState);
        Assert.Equal(1, repo.CommitAttempts);
        Assert.Equal(committed ? 1 : 0, repo.Writes);
        if (committed) Assert.Equal(TodoItemVersion.Of(Assert.Single(repo.Items)), repeated.Payload.GetProperty("version").GetString());
    }
    private sealed class Repository : ITodoRepository, ITodoAgentCommandRepository
    {
        public List<TodoItem> Items { get; } = [];
        private readonly Dictionary<string, TodoAgentCommitResult> receipts = [];
        public int Writes { get; private set; }
        public bool ThrowAfterCommit { get; set; }
        public bool ThrowBeforeCommit { get; set; }
        public int CommitAttempts { get; private set; }
        public TodoAgentCommitResult? ReadAgentReceipt(ToolScope scope, string key, string fingerprint) =>
            receipts.TryGetValue(key, out var receipt) ? receipt with { Kind = TodoAgentCommitKind.AlreadyCommitted } : null;
        public void Save(TodoItem item) { Items.RemoveAll(t => t.Id == item.Id); Items.Add(item); }
        public TodoItem? Get(string id) => Items.SingleOrDefault(t => t.Id == id);
        public IReadOnlyList<TodoItem> List(TodoStatus? status = null) => Items.Where(t => status is null || t.Status == status).ToArray();
        public IReadOnlyList<TodoItem> ListCompletedOn(DateOnly date) => [];
        public void Delete(string id) => Items.RemoveAll(t => t.Id == id);
        public TodoAgentCommitResult CommitAgentCommand(TodoAgentCommit command)
        {
            CommitAttempts++;
            if (ThrowBeforeCommit) throw new IOException("synthetic failure");
            if (receipts.TryGetValue(command.IdempotencyKey, out var previous)) return previous with { Kind = TodoAgentCommitKind.AlreadyCommitted };
            if (!TodoItemValueComparer.Equals(Get(command.Replacement.Id), command.Expected)) return new(TodoAgentCommitKind.Conflict);
            Save(command.Replacement); Writes++;
            var result = new TodoAgentCommitResult(TodoAgentCommitKind.Committed, command.Replacement.Id, TodoItemVersion.Of(command.Replacement));
            receipts[command.IdempotencyKey] = result;
            if (ThrowAfterCommit) throw new IOException("synthetic acknowledgement failure");
            return result;
        }
    }
}
