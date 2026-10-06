using System.Security.Cryptography;
using System.Text.Json;
using FgoPet.App.Dialogue;
using FgoPet.App.Services;
using FgoPet.Core.Todo;
using FgoPet.Extensibility;

namespace FgoPet.Plugin.Todo;

/// <summary>Native adapters freeze and confirm through the Todo owner; they never infer user permission from text.</summary>
public static class NativeTodoTools
{
    public static IReadOnlyList<IToolProvider> Create(TodoApplicationService todos, ITodoRepository repository,
        TodoProposalService proposals, TimeProvider time)
    {
        var workflow = new Workflow(todos, repository, proposals, time);
        return [new Reader(todos), new Command("todo.create", workflow), new Command("todo.update", workflow), new Command("todo.complete", workflow)];
    }
    private static ToolResult Failure(string code, ToolExecutionState state = ToolExecutionState.NotExecuted) =>
        new(false, JsonSerializer.SerializeToElement(new { }), code) { ExecutionState = state };
    private sealed class Reader(TodoApplicationService todos) : IToolProvider
    {
        public ToolDescriptor Descriptor { get; } = new("todo.list", "Read real local Todo state and exact item versions.",
            """{"type":"object","properties":{"limit":{"type":"integer","minimum":1,"maximum":20},"status":{"enum":["all","planned","active","completed"]}},"additionalProperties":false}""", ToolEffect.ReadOnly);
        public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var limit = invocation.Arguments.TryGetProperty("limit", out var value) ? value.GetInt32() : 10;
                var status = invocation.Arguments.TryGetProperty("status", out value) ? value.GetString() : "all";
                if (limit is < 1 or > 20 || status is not ("all" or "planned" or "active" or "completed")) return ValueTask.FromResult(Failure("TOOL_INVALID_ARGUMENTS"));
                var items = todos.ListActive().Concat(todos.ListHistory()).Where(item => status == "all" || item.Status.ToString().Equals(status, StringComparison.OrdinalIgnoreCase)).Take(limit + 1).ToArray();
                token.ThrowIfCancellationRequested();
                return ValueTask.FromResult(new ToolResult(true, JsonSerializer.SerializeToElement(new {
                    items = items.Take(limit).Select(item => new { id = item.Id, title = item.Title, status = item.Status.ToString().ToLowerInvariant(),
                        version = TodoItemVersion.Of(item), dueAt = item.DueAt, steps = item.Steps.Count, incompleteSteps = item.Steps.Count(s => !s.IsCompleted) }),
                    truncated = items.Length > limit })));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { return ValueTask.FromResult(Failure("TODO_UNAVAILABLE")); }
        }
    }
    private sealed class Command(string name, Workflow workflow) : IToolProvider, IToolBusinessConfirmationProvider
    {
        public ToolDescriptor Descriptor { get; } = new(name, "Freeze a Todo change for exact user confirmation. No write occurs before owner confirmation.", name switch {
            "todo.create" => """
                {"type":"object","properties":{"title":{"type":"string","minLength":1,"maxLength":500},"description":{"type":"string","maxLength":4000},"priority":{"enum":["low","normal","high"]},"due_at":{"type":"string","maxLength":64},"steps":{"type":"array","maxItems":20,"items":{"type":"object","properties":{"title":{"type":"string","minLength":1,"maxLength":200}},"required":["title"],"additionalProperties":false}}},"required":["title"],"additionalProperties":false}
                """,
            "todo.update" => """
                {"type":"object","properties":{"id":{"type":"string","minLength":1,"maxLength":128},"expectedVersion":{"type":"string","minLength":64,"maxLength":64},"title":{"type":"string","minLength":1,"maxLength":500},"description":{"type":"string","maxLength":4000}},"required":["id","expectedVersion","title"],"additionalProperties":false}
                """,
            _ => """
                {"type":"object","properties":{"id":{"type":"string","minLength":1,"maxLength":128},"expectedVersion":{"type":"string","minLength":64,"maxLength":64},"confirmIncompleteSteps":{"type":"boolean"}},"required":["id","expectedVersion"],"additionalProperties":false}
                """
        }, ToolEffect.Command);
        public ToolBusinessConfirmation PrepareConfirmation(ToolInvocation invocation, CancellationToken token) => workflow.Prepare(name, invocation, token);
        public void ValidateConfirmation(ToolInvocation invocation, ToolBusinessConfirmation confirmation, CancellationToken token) => workflow.Validate(name, invocation, confirmation, token);
        public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken token) => ValueTask.FromResult(workflow.Confirm(name, invocation, token));
    }
    private sealed class Workflow(TodoApplicationService todos, ITodoRepository repository, TodoProposalService proposals, TimeProvider time)
    {
        private readonly object gate = new();
        private readonly Dictionary<string, Draft> drafts = [];
        private static string Arguments(string name, ToolInvocation invocation) => Convert.ToHexString(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(new { name, invocation.Scope, invocation.Arguments })));
        public ToolBusinessConfirmation Prepare(string name, ToolInvocation invocation, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            lock (gate)
            {
                var execution = invocation.ExecutionContext ?? throw new ToolBusinessConfirmationException("TODO_CONTEXT_REQUIRED");
                var argumentHash = Arguments(name, invocation);
                if (drafts.TryGetValue(execution.IdempotencyKey, out var prior))
                {
                    if (prior.Arguments != argumentHash) throw new ToolBusinessConfirmationException("TODO_CONFLICT");
                    Validate(name, invocation, prior.Binding, token); return prior.Binding;
                }
                foreach (var key in drafts.Where(pair => pair.Value.Committed || time.GetUtcNow() - pair.Value.CreatedAt >= TimeSpan.FromHours(1)).Select(pair => pair.Key).ToArray()) drafts.Remove(key);
                if (drafts.Count >= 64 || repository is not ITodoAgentCommandRepository) throw new ToolBusinessConfirmationException("TODO_UNAVAILABLE");
                TodoItem? expected = null; TodoItem replacement; PendingTodoDraft? legacy = null;
                var args = invocation.Arguments;
                var at = time.GetUtcNow();
                try
                {
                    if (name == "todo.create")
                    {
                        var proposal = proposals.Parse(args.GetRawText()).Single();
                        replacement = new("native-" + execution.IdempotencyKey[..32], proposal.Title, proposal.Description, proposal.Priority,
                            proposal.DueAt, at, at, steps: proposal.StepTitles.Select((title, i) => new TodoStep("native-" + execution.IdempotencyKey[..24] + "-" + i, title, i)).ToArray());
                        legacy = proposals.Drafts.ReplaceForAgent(invocation.Scope.ConversationId, invocation.Scope.RoleId, [proposal]);
                    }
                    else
                    {
                        expected = todos.Get(args.GetProperty("id").GetString()!) ?? throw new ToolBusinessConfirmationException("TODO_NOT_FOUND");
                        if (TodoItemVersion.Of(expected) != args.GetProperty("expectedVersion").GetString() || expected.Status != TodoStatus.Planned)
                            throw new ToolBusinessConfirmationException("TODO_CONFLICT");
                        if (name == "todo.update") replacement = new(expected.Id, args.GetProperty("title").GetString()!,
                            args.TryGetProperty("description", out var description) ? description.GetString() : expected.Description,
                            expected.Priority, expected.DueAt, expected.CreatedAt, at, expected.Status, expected.CompletedAt, expected.Steps);
                        else
                        {
                            if (expected.Steps.Any(step => !step.IsCompleted) && !(args.TryGetProperty("confirmIncompleteSteps", out var confirm) && confirm.GetBoolean()))
                                throw new ToolBusinessConfirmationException("TODO_CHECKLIST_CONFIRMATION_REQUIRED");
                            replacement = expected.Complete(at);
                        }
                    }
                }
                catch (Exception error) when (error is ArgumentException or FormatException or InvalidOperationException or KeyNotFoundException)
                { throw new ToolBusinessConfirmationException("TODO_INVALID_CHANGE"); }
                var mutation = new TodoAgentCommit(invocation.Scope, execution.IdempotencyKey, argumentHash, expected, replacement);
                var binding = new ToolBusinessConfirmation(legacy?.DraftId ?? "mutation-" + Guid.NewGuid().ToString("N"), legacy?.Version ?? 1,
                    Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(mutation))));
                drafts[execution.IdempotencyKey] = new(binding, mutation, argumentHash, at, legacy);
                return binding;
            }
        }
        public void Validate(string name, ToolInvocation invocation, ToolBusinessConfirmation confirmation, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            lock (gate)
            {
                if (invocation.ExecutionContext is not { } execution || !drafts.TryGetValue(execution.IdempotencyKey, out var draft) ||
                    draft.Binding != confirmation || draft.Mutation.Scope != invocation.Scope || draft.Arguments != Arguments(name, invocation) ||
                    time.GetUtcNow() - draft.CreatedAt >= TimeSpan.FromHours(1)) throw new ToolBusinessConfirmationException("TODO_DRAFT_STALE");
                if (draft.Committed || draft.Unknown) return;
                if (!TodoItemValueComparer.Equals(todos.Get(draft.Mutation.Replacement.Id), draft.Mutation.Expected)) throw new ToolBusinessConfirmationException("TODO_CONFLICT");
                if (draft.Legacy is not null && proposals.Drafts.Get(invocation.Scope.ConversationId, invocation.Scope.RoleId) is null)
                    throw new ToolBusinessConfirmationException("TODO_DRAFT_STALE");
                if (draft.Legacy is { } original)
                {
                    var current = proposals.Drafts.Get(invocation.Scope.ConversationId, invocation.Scope.RoleId)!;
                    if (current.DraftId != original.DraftId || current.Version != original.Version || current.Status != TodoDraftStatus.Pending)
                        throw new ToolBusinessConfirmationException("TODO_DRAFT_STALE");
                }
            }
        }
        public ToolResult Confirm(string name, ToolInvocation invocation, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            lock (gate)
            {
                if (invocation.ExecutionContext?.BusinessConfirmation is not { } confirmation) return Failure("TODO_CONFIRMATION_REQUIRED");
                var attempted = false;
                Draft? draft = null;
                try
                {
                    Validate(name, invocation, confirmation, token);
                    draft = drafts[invocation.ExecutionContext.IdempotencyKey];
                    if (draft.Result is { } cached) return cached;
                    if (draft.Unknown)
                    {
                        var receipt = (repository as ITodoAgentCommandRepository)?.ReadAgentReceipt(
                            invocation.Scope, draft.Mutation.IdempotencyKey, draft.Mutation.Fingerprint);
                        if (receipt?.Kind is not (TodoAgentCommitKind.Committed or TodoAgentCommitKind.AlreadyCommitted))
                            return Failure("TODO_EXECUTION_UNKNOWN", ToolExecutionState.Unknown);
                        return Finish(draft, receipt);
                    }
                    attempted = true;
                    var result = todos.CommitAgentCommand(draft.Mutation);
                    if (result.Kind is not (TodoAgentCommitKind.Committed or TodoAgentCommitKind.AlreadyCommitted))
                        return Failure(result.Kind == TodoAgentCommitKind.Conflict ? "TODO_CONFLICT" : "TODO_UNAVAILABLE");
                    return Finish(draft, result);
                }
                catch (ToolBusinessConfirmationException) { return Failure("TODO_DRAFT_STALE"); }
                catch (OperationCanceledException) { throw; }
                catch (Exception)
                {
                    if (draft?.Result is { } committed) return committed;
                    if (attempted && draft is not null) draft.Unknown = true;
                    var unknown = attempted || draft?.Unknown == true;
                    return Failure(unknown ? "TODO_EXECUTION_UNKNOWN" : "TODO_UNAVAILABLE", unknown ? ToolExecutionState.Unknown : ToolExecutionState.NotExecuted);
                }
            }
        }
        private ToolResult Finish(Draft draft, TodoAgentCommitResult result)
        {
            draft.Result = new(true, JsonSerializer.SerializeToElement(new { id = result.ItemId, version = result.Version,
                alreadyCommitted = result.Kind == TodoAgentCommitKind.AlreadyCommitted })) { ExecutionState = ToolExecutionState.Committed };
            draft.Committed = true;
            if (draft.Legacy is { } legacy) proposals.Drafts.Cancel(draft.Mutation.Scope.ConversationId, draft.Mutation.Scope.RoleId, legacy.DraftId);
            return draft.Result;
        }
        private sealed record Draft(ToolBusinessConfirmation Binding, TodoAgentCommit Mutation, string Arguments,
            DateTimeOffset CreatedAt, PendingTodoDraft? Legacy)
        { public bool Committed { get; set; } public bool Unknown { get; set; } public ToolResult? Result { get; set; } }
    }
}
