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
    private static IReadOnlyList<IToolProvider> Tools(ITodoRepository repo)
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
    [Theory]
    [InlineData(TodoStatus.Planned, "planned")]
    [InlineData(TodoStatus.Active, "active")]
    [InlineData(TodoStatus.Completed, "completed")]
    public async Task Get_reads_the_exact_owner_details_and_version_without_writing(TodoStatus status, string statusKey)
    {
        var at = DateTimeOffset.Parse("2026-10-19T08:00:00+08:00");
        var item = new TodoItem("detail", "Read this task", "<fixture>Keep this text</fixture>", TodoPriority.High,
            at.AddDays(1), at, at.AddMinutes(2), status, status == TodoStatus.Completed ? at.AddMinutes(2) : null,
            [new("first", "Prepare", 0), new("second", "Check", 1, isCompleted: true)]);
        var repo = new ReadRepository(item);
        var get = GetTool(repo);

        var result = await get.InvokeAsync(new ToolInvocation(Scope, JsonSerializer.SerializeToElement(new { id = "detail" })), default);

        Assert.True(result.Success);
        Assert.Equal(ToolEffect.ReadOnly, get.Descriptor.Effect);
        Assert.False(get is IToolBusinessConfirmationProvider);
        Assert.Equal("detail", result.Payload.GetProperty("id").GetString());
        Assert.Equal("Read this task", result.Payload.GetProperty("title").GetString());
        Assert.Equal("<fixture>Keep this text</fixture>", result.Payload.GetProperty("description").GetString());
        Assert.Equal("high", result.Payload.GetProperty("priority").GetString());
        Assert.Equal(statusKey, result.Payload.GetProperty("status").GetString());
        Assert.Equal(at.AddDays(1), result.Payload.GetProperty("dueAt").GetDateTimeOffset());
        Assert.Equal(at, result.Payload.GetProperty("createdAt").GetDateTimeOffset());
        Assert.Equal(at.AddMinutes(2), result.Payload.GetProperty("updatedAt").GetDateTimeOffset());
        if (status == TodoStatus.Completed)
            Assert.Equal(at.AddMinutes(2), result.Payload.GetProperty("completedAt").GetDateTimeOffset());
        else Assert.Equal(JsonValueKind.Null, result.Payload.GetProperty("completedAt").ValueKind);
        Assert.Equal(TodoItemVersion.Of(item), result.Payload.GetProperty("version").GetString());
        var steps = result.Payload.GetProperty("steps").EnumerateArray().ToArray();
        Assert.Equal(2, steps.Length);
        Assert.Equal("first", steps[0].GetProperty("id").GetString());
        Assert.Equal("Prepare", steps[0].GetProperty("title").GetString());
        Assert.Equal(0, steps[0].GetProperty("order").GetInt32());
        Assert.False(steps[0].GetProperty("isCompleted").GetBoolean());
        Assert.Equal("second", steps[1].GetProperty("id").GetString());
        Assert.Equal("Check", steps[1].GetProperty("title").GetString());
        Assert.Equal(1, steps[1].GetProperty("order").GetInt32());
        Assert.True(steps[1].GetProperty("isCompleted").GetBoolean());
        Assert.Same(item, repo.Item);
        Assert.Equal("detail", repo.LastReadId);
        Assert.Equal(1, repo.Reads);
        Assert.Equal(0, repo.WriteAttempts);
    }

    [Fact]
    public async Task Get_uses_the_same_version_as_list_and_reads_owner_edits_fresh()
    {
        var repo = new Repository();
        var application = new TodoApplicationService(repo, TimeProvider.System);
        var item = application.Create("before", null, TodoPriority.Normal, null);
        var tools = Tools(repo);
        var get = Assert.Single(tools, tool => tool.Descriptor.Name == "todo.get");
        var list = Assert.Single(tools, tool => tool.Descriptor.Name == "todo.list");
        var firstList = await list.InvokeAsync(Invocation(new { }), default);
        var first = await get.InvokeAsync(Invocation(new { id = item.Id }), default);
        Assert.True(first.Success);
        Assert.Equal(JsonValueKind.Null, first.Payload.GetProperty("description").ValueKind);
        Assert.Equal(JsonValueKind.Null, first.Payload.GetProperty("dueAt").ValueKind);
        Assert.Equal(0, first.Payload.GetProperty("steps").GetArrayLength());
        Assert.Equal(firstList.Payload.GetProperty("items")[0].GetProperty("version").GetString(), first.Payload.GetProperty("version").GetString());

        application.Update(item, "after", "new detail");
        var changedList = await list.InvokeAsync(Invocation(new { }), default);
        var changed = await get.InvokeAsync(Invocation(new { id = item.Id }), default);

        Assert.True(changed.Success);
        Assert.Equal("after", changed.Payload.GetProperty("title").GetString());
        Assert.Equal("new detail", changed.Payload.GetProperty("description").GetString());
        Assert.NotEqual(first.Payload.GetProperty("version").GetString(), changed.Payload.GetProperty("version").GetString());
        Assert.Equal(changedList.Payload.GetProperty("items")[0].GetProperty("version").GetString(), changed.Payload.GetProperty("version").GetString());
    }

    [Fact]
    public async Task Get_reports_not_found_without_returning_another_item()
    {
        var at = DateTimeOffset.UnixEpoch;
        var repo = new ReadRepository(new TodoItem("other", "Do not return this", null, TodoPriority.Low, null, at, at));

        var result = await GetTool(repo).InvokeAsync(Invocation(new { id = "missing" }), default);

        Assert.False(result.Success);
        Assert.Equal("TODO_NOT_FOUND", result.ErrorCode);
        Assert.Equal(ToolExecutionState.NotExecuted, result.ExecutionState);
        Assert.Equal("{}", result.Payload.GetRawText());
        Assert.Equal("missing", repo.LastReadId);
        Assert.Equal(1, repo.Reads);
        Assert.Equal(0, repo.WriteAttempts);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"detail\"")]
    [InlineData("{}")]
    [InlineData("{\"id\":null}")]
    [InlineData("{\"id\":1}")]
    [InlineData("{\"id\":true}")]
    [InlineData("{\"id\":\"\"}")]
    [InlineData("{\"id\":\"   \"}")]
    [InlineData("{\"ID\":\"detail\"}")]
    [InlineData("{\"id\":\"detail\",\"id\":\"other\"}")]
    [InlineData("{\"id\":\"detail\",\"scope\":\"other\"}")]
    public async Task Get_rejects_invalid_argument_shapes_before_reading(string json)
    {
        var repo = new ReadRepository();
        var get = GetTool(repo);
        using var arguments = JsonDocument.Parse(json);

        var result = await get.InvokeAsync(new ToolInvocation(Scope, arguments.RootElement), default);

        Assert.False(result.Success);
        Assert.Equal("TOOL_INVALID_ARGUMENTS", result.ErrorCode);
        Assert.Equal(ToolExecutionState.NotExecuted, result.ExecutionState);
        Assert.Equal(0, repo.Reads);
        Assert.Equal(0, repo.WriteAttempts);
    }

    [Fact]
    public async Task Get_rejects_an_overlong_id_before_reading()
    {
        var repo = new ReadRepository();

        var result = await GetTool(repo).InvokeAsync(Invocation(new { id = new string('x', 129) }), default);

        Assert.Equal("TOOL_INVALID_ARGUMENTS", result.ErrorCode);
        Assert.Equal(0, repo.Reads);
        Assert.Equal(0, repo.WriteAttempts);
    }

    [Fact]
    public async Task Get_rejects_an_oversized_arguments_envelope_before_reading()
    {
        var repo = new ReadRepository();
        var get = GetTool(repo);
        using var arguments = JsonDocument.Parse("{\"id\":\"detail\"" + new string(' ', 4096) + "}");

        var result = await get.InvokeAsync(new ToolInvocation(Scope, arguments.RootElement), default);

        Assert.Equal("TOOL_INVALID_ARGUMENTS", result.ErrorCode);
        Assert.Equal(0, repo.Reads);
        Assert.Equal(0, repo.WriteAttempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Get_propagates_cancellation_before_or_after_the_owner_read(bool duringRead)
    {
        var at = DateTimeOffset.UnixEpoch;
        var repo = new ReadRepository(new TodoItem("detail", "Read", null, TodoPriority.Normal, null, at, at));
        var get = GetTool(repo);
        using var cancellation = new CancellationTokenSource();
        if (duringRead) repo.AfterRead = cancellation.Cancel;
        else cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            get.InvokeAsync(Invocation(new { id = "detail" }), cancellation.Token).AsTask());

        Assert.Equal(duringRead ? 1 : 0, repo.Reads);
        Assert.Equal(0, repo.WriteAttempts);
    }

    [Fact]
    public async Task Get_sanitizes_repository_failures_without_returning_exception_details()
    {
        var repo = new ReadRepository { ReadFailure = new IOException("synthetic detail must remain private") };

        var result = await GetTool(repo).InvokeAsync(Invocation(new { id = "detail" }), default);

        Assert.False(result.Success);
        Assert.Equal("TODO_UNAVAILABLE", result.ErrorCode);
        Assert.Equal(ToolExecutionState.NotExecuted, result.ExecutionState);
        Assert.Equal("{}", result.Payload.GetRawText());
        Assert.Equal(1, repo.Reads);
        Assert.Equal(0, repo.WriteAttempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Get_returns_complete_bounded_details_or_an_explicit_size_failure(bool escapeHeavy)
    {
        var at = DateTimeOffset.UnixEpoch;
        var text = escapeHeavy ? '\u0001' : '测';
        var item = new TodoItem(new string('i', 128), new string(text, 500), new string(text, 4000), TodoPriority.Normal, null, at, at,
            steps: Enumerable.Range(0, 20).Select(i => new TodoStep(i.ToString("D2") + new string(escapeHeavy ? '\u0001' : 's', 126), new string(text, 200), i)).ToArray());
        var repo = new ReadRepository(item);

        var result = await GetTool(repo).InvokeAsync(Invocation(new { id = item.Id }), default);

        if (escapeHeavy)
        {
            Assert.False(result.Success);
            Assert.Equal("TOOL_OUTPUT_TOO_LARGE", result.ErrorCode);
            Assert.Equal(ToolExecutionState.NotExecuted, result.ExecutionState);
            Assert.Equal("{}", result.Payload.GetRawText());
        }
        else
        {
            Assert.True(result.Success);
            Assert.Equal(item.Title, result.Payload.GetProperty("title").GetString());
            Assert.Equal(item.Description, result.Payload.GetProperty("description").GetString());
            Assert.Equal(20, result.Payload.GetProperty("steps").GetArrayLength());
            Assert.Equal(item.Steps[19].Title, result.Payload.GetProperty("steps")[19].GetProperty("title").GetString());
            Assert.Equal(item.Steps[19].Id, result.Payload.GetProperty("steps")[19].GetProperty("id").GetString());
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(result.Payload.GetRawText()) <= 64 * 1024);
        }
        Assert.Equal(0, repo.WriteAttempts);
    }

    private static IToolProvider GetTool(ITodoRepository repo) =>
        Assert.Single(Tools(repo), tool => tool.Descriptor.Name == "todo.get");

    private sealed class ReadRepository(TodoItem? item = null) : ITodoRepository
    {
        public TodoItem? Item { get; } = item;
        public int Reads { get; private set; }
        public int WriteAttempts { get; private set; }
        public string? LastReadId { get; private set; }
        public Action? AfterRead { get; set; }
        public Exception? ReadFailure { get; init; }
        public TodoItem? Get(string id)
        {
            Reads++;
            LastReadId = id;
            if (ReadFailure is { } error) throw error;
            var result = Item?.Id == id ? Item : null;
            AfterRead?.Invoke();
            return result;
        }
        public void Save(TodoItem todo) { WriteAttempts++; throw new InvalidOperationException("Read attempted a write."); }
        public void Delete(string id) { WriteAttempts++; throw new InvalidOperationException("Read attempted a delete."); }
        public bool TryUpdateLocal(TodoItem expected, TodoItem? replacement) { WriteAttempts++; throw new InvalidOperationException("Read attempted an update."); }
        public IReadOnlyList<TodoItem> List(TodoStatus? status = null) => throw new InvalidOperationException("Read attempted a list.");
        public IReadOnlyList<TodoItem> ListCompletedOn(DateOnly date) => throw new InvalidOperationException("Read attempted a history query.");
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
