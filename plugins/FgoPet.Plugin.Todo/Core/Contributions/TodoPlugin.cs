using System.Text.Json;
using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Todo;
using FgoPet.Extensibility;

namespace FgoPet.Plugin.Todo;

/// <summary>Owns Todo's conversation integration; the kernel receives only neutral results and opaque scope keys.</summary>
public sealed class TodoPlugin : IFgoPetPlugin, IToolProvider, IConversationContextProvider,
    IConversationContinuationProvider, ITextReplyInterpreter, IDisposable
{
    private readonly ITodoProposalReader? _proposals;
    private readonly ITodoDraftWorkflow _drafts;
    private CancellationToken _stopping;
    private bool _closed;
    public TodoPlugin(ITodoConversationPort? proposals = null, ITodoDraftWorkflow? drafts = null)
    {
        _proposals = proposals;
        _drafts = drafts ?? proposals?.Drafts ?? throw new ArgumentException("Todo requires its draft owner.");
        Contributions = new(proposals is null ? [] : [this], [new("todo.workspace", "待办")], [])
        { Contexts = proposals is null ? [] : [this], Continuations = [this], TextInterpreters = proposals is null ? [] : [this], Prompts = proposals is null ? [] : [new TodoPromptProvider()] };
    }
    public PluginManifest Manifest { get; } = new("firstparty.todo", "1.0.0", 1, []);
    public PluginContributions Contributions { get; }
    public ToolDescriptor Descriptor { get; } = TodoToolContracts.CreateSubmitTodoProposals();
    public ValueTask StartAsync(CancellationToken stoppingToken) { _stopping = stoppingToken; return ValueTask.CompletedTask; }
    public ValueTask StopAsync(CancellationToken cancellationToken) { _closed = true; return ValueTask.CompletedTask; }
    public ValueTask DisposeAsync() { _closed = true; return ValueTask.CompletedTask; }
    public void Dispose() => _closed = true;
    private void CheckRunning()
    {
        _stopping.ThrowIfCancellationRequested();
        if (_closed) throw new OperationCanceledException("Todo capability is stopped.");
    }

    public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        CheckRunning();
        cancellationToken.ThrowIfCancellationRequested();
        var parsed = _proposals?.TryParseToolCall(invocation.Arguments) ?? ToolCallProposalResult.Fail(TodoToolCallFailure.InvalidJson);
        if (!parsed.Success) return ValueTask.FromResult(new ToolResult(false, default, "TODO_INVALID_PROPOSAL")
        { Conversation = new(CapabilityOutcome.InvalidToolCall, Detail: DescribeFailure(parsed)) });
        var scope = invocation.Scope;
        var proposals = parsed.Proposals!;
        var reply = BuildDraftReply(proposals);
        if (reply.Length > ConversationContributionLimits.ReplyChars || !TodoContinuationState.FitsPromptBudget(proposals))
            return ValueTask.FromResult(new ToolResult(false, default, "TODO_DRAFT_TOO_LARGE") { Conversation = TooLarge(scope) });
        var draft = _drafts.Replace(scope.ConversationId, scope.RoleId, proposals);
        return ValueTask.FromResult(new ToolResult(true, invocation.Arguments.Clone())
        {
            Conversation = new(CapabilityOutcome.ProposalsReady, reply,
                StructuredPayload: invocation.Arguments.GetRawText(), DraftId: draft.DraftId, DraftVersion: draft.Version)
        });
    }

    public string BuildContext(ToolScope scope, string userMessage) => _proposals?.BuildRuntimeState(userMessage) ?? string.Empty;
    public string? GetPromptState(ToolScope scope) => _drafts.GetPromptState(scope.ConversationId, scope.RoleId);
    public void ClearRole(string roleId) => _drafts.ClearServant(roleId);
    public void RemoveConversation(ToolScope scope) => _drafts.Cancel(scope.ConversationId, scope.RoleId);

    public ConversationContributionResult? TryHandleInput(ToolScope scope, string userMessage)
    {
        CheckRunning();
        var pending = _drafts.Get(scope.ConversationId, scope.RoleId);
        if (pending is null) return null;
        // The entire utterance must authorize the operation. Questions, quotations and negations do not.
        var normalized = userMessage.Trim().TrimEnd('。', '.', '！', '!');
        if (normalized is "取消" or "取消草稿" or "取消待办" or "不创建" or "不用了" or "算了")
        {
            _drafts.Cancel(scope.ConversationId, scope.RoleId, pending.DraftId);
            return new(CapabilityOutcome.Cancelled, "好的，这份待办草稿已取消，没有写入待办。");
        }
        if (normalized is not ("确认" or "确认创建" or "确定创建" or "加入待办" or "确认加入待办" or "确认全部创建")) return null;
        var committed = _drafts.Confirm(scope.ConversationId, scope.RoleId, pending.DraftId, pending.Version,
            $"todo-confirm:{scope.ConversationId}:{pending.DraftId}:{pending.Version}");
        return committed.Kind switch
        {
            TodoDraftResultKind.Committed or TodoDraftResultKind.AlreadyCommitted when committed.Todo is not null =>
                new(CapabilityOutcome.Confirmed, FormatCommittedReply(committed.Todos), CreatedItemId: committed.Todo.Id, WorkspaceId: "todo.workspace"),
            TodoDraftResultKind.Unknown => new(CapabilityOutcome.CommitUnknown, "待办写入结果暂时无法确认，草稿已保留，请稍后重试。"),
            _ => new(CapabilityOutcome.ConfirmationUnknown, "这份待办草稿已发生变化，请重新确认当前内容。")
        };
    }

    public ConversationContributionResult? TryInterpretReply(ToolScope scope, string reply)
    {
        CheckRunning();
        try
        {
            var proposals = _proposals?.ParseEnvelope(reply);
            if (proposals is not { Count: > 0 }) return null;
            var visibleReply = BuildDraftReply(proposals);
            if (visibleReply.Length > ConversationContributionLimits.ReplyChars || !TodoContinuationState.FitsPromptBudget(proposals))
                return TooLarge(scope);
            var draft = _drafts.Replace(scope.ConversationId, scope.RoleId, proposals);
            return new(CapabilityOutcome.TextFallback, Reply: visibleReply, StructuredPayload: reply, DraftId: draft.DraftId, DraftVersion: draft.Version);
        }
        catch (FormatException) { return null; }
    }

    private ConversationContributionResult TooLarge(ToolScope scope)
    {
        var previous = _drafts.Get(scope.ConversationId, scope.RoleId);
        return new(CapabilityOutcome.InvalidToolCall, "这份待办草稿过长，请分批整理；本次没有创建或替换草稿。",
            DraftId: previous?.DraftId, DraftVersion: previous?.Version);
    }

    private static string DescribeFailure(ToolCallProposalResult parsed) => parsed.Failure switch
    {
        TodoToolCallFailure.MissingTodos => "工具参数缺少 todos 数组。",
        TodoToolCallFailure.TooMany => "提案超过 10 条上限。",
        TodoToolCallFailure.NotPlanning => "提案内容被安全校验拒绝。",
        TodoToolCallFailure.UnsupportedField => TodoProposalService.IsKnownExecutionField(parsed.FieldName)
            ? $"提案包含不支持的执行字段：{parsed.FieldName}。" : "提案包含不支持的执行字段。",
        _ => "工具调用无法解析。"
    };

    private static string BuildDraftReply(IReadOnlyList<TodoProposal> proposals) => "我整理了以下待办草稿：\n"
        + string.Join("\n", proposals.Select((proposal, index) => $"{index + 1}. {proposal.Title}（步骤："
            + (proposal.StepTitles.Count == 0 ? "无步骤" : string.Join("、", proposal.StepTitles.Select((title, stepIndex) => $"{stepIndex + 1}. {title}"))) + "）"))
        + "\n尚未创建；你可以继续修改，确认后才会加入待办。";

    private static string FormatCommittedReply(IReadOnlyList<TodoItem> todos) => todos.Count == 1
        ? $"已创建待办“{todos[0].Title}”，包含 {todos[0].Steps.Count} 个步骤。"
        : $"已创建 {todos.Count} 个待办：\n" + string.Join("\n", todos.Select((todo, index) => $"{index + 1}. “{todo.Title}”（{todo.Steps.Count} 个步骤）"));
}
