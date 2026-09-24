using System.Text.Json;

// Kept in the existing namespace for source compatibility during the assembly migration.
// These types are owned by Work/Contracts and compiled by Core, not by Dialogue or Work.Todo.
namespace FgoPet.App.Dialogue;

/// <summary>
/// Read-only proposal parsing and redacted context. Implementations must reject unsafe or
/// unsupported model fields. None of these operations may create Todos or dispatch agents.
/// </summary>
public interface ITodoProposalReader
{
    IReadOnlyList<TodoProposal>? ParseEnvelope(string modelResponse);
    ToolCallProposalResult TryParseToolCall(JsonElement arguments);
    string BuildRuntimeState(string userMessage);
}

/// <summary>
/// Work's conversation boundary. Drafts is the shared, session-scoped workflow used by
/// both dialogue and confirmation UI; it must return the same instance for this port's lifetime.
/// Direct creation methods are intentionally absent: writes require a draft ID, version,
/// scope and idempotency key through ITodoDraftWorkflow. Parsing alone never authorizes a write.
/// </summary>
public interface ITodoConversationPort : ITodoProposalReader
{
    ITodoDraftWorkflow Drafts { get; }
}

public enum TodoToolCallFailure
{
    None,
    InvalidJson,
    MissingTodos,
    UnsupportedField,
    TooMany,
    NotPlanning,
}

public sealed record ToolCallProposalResult(
    bool Success,
    IReadOnlyList<TodoProposal>? Proposals = null,
    TodoToolCallFailure Failure = TodoToolCallFailure.None,
    string? FieldName = null)
{
    public static ToolCallProposalResult Ok(IReadOnlyList<TodoProposal> proposals) => new(true, proposals);
    public static ToolCallProposalResult Fail(TodoToolCallFailure failure, string? fieldName = null) => new(false, null, failure, fieldName);
}
