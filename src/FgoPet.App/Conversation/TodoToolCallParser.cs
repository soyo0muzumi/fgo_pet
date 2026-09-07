using System.Text.Json;

namespace FgoPet.App.Dialogue;

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

public partial class TodoProposalService
{
    /// <summary>
    /// Parses the arguments element of a submit_todo_proposals tool call. The
    /// same boundaries as the text envelope apply here: unknown or denied fields
    /// are rejected by the parser regardless of the schema the model claimed.
    /// </summary>
    public ToolCallProposalResult TryParseToolCall(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            return ToolCallProposalResult.Fail(TodoToolCallFailure.InvalidJson);
        }

        if (!arguments.TryGetProperty("todos", out var todosElement) || todosElement.ValueKind != JsonValueKind.Array)
        {
            return ToolCallProposalResult.Fail(TodoToolCallFailure.MissingTodos);
        }

        if (arguments.EnumerateObject().Any(property => property.Name != "todos"))
        {
            var unsupported = arguments.EnumerateObject().First(property => property.Name != "todos").Name;
            return ToolCallProposalResult.Fail(TodoToolCallFailure.UnsupportedField, unsupported);
        }

        var items = todosElement.EnumerateArray().ToArray();
        if (items.Length == 0)
        {
            return ToolCallProposalResult.Fail(TodoToolCallFailure.MissingTodos);
        }

        if (items.Length > 10)
        {
            return ToolCallProposalResult.Fail(TodoToolCallFailure.TooMany);
        }

        try
        {
            return ToolCallProposalResult.Ok(items.Select(ParseOne).ToArray());
        }
        catch (FormatException error)
        {
            var field = error.Message.Contains('\'')
                ? error.Message.Split('\'', StringSplitOptions.RemoveEmptyEntries).Skip(1).FirstOrDefault()
                : null;
            var failure = error.Message.Contains("unsafe")
                ? TodoToolCallFailure.NotPlanning
                : TodoToolCallFailure.UnsupportedField;
            return ToolCallProposalResult.Fail(failure, field);
        }
    }
}
