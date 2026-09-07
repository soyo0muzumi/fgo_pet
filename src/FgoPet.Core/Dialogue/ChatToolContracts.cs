using System.Text.Json;

namespace FgoPet.Core.Dialogue;

public sealed record ChatToolDefinition
{
    public const string FunctionType = "function";

    public ChatToolDefinition(string name, string description, string parametersJson)
    {
        Name = Phase3Validation.Id(name, nameof(name), 64);
        Description = Phase3Validation.Text(description, nameof(description), 2_048);
        try
        {
            using var document = JsonDocument.Parse(parametersJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("Tool parameters must be a JSON object.", nameof(parametersJson));
            }

            Parameters = document.RootElement.Clone();
        }
        catch (JsonException error)
        {
            throw new ArgumentException("Tool parameters must be valid JSON.", nameof(parametersJson), error);
        }
    }

    public string Type => FunctionType;
    public string Name { get; }
    public string Description { get; }
    public JsonElement Parameters { get; }
}

public sealed record ChatToolCallDelta
{
    public ChatToolCallDelta(int index, string? id = null, string? name = null, string? argumentsDelta = null)
    {
        if (index < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        Index = index;
        Id = string.IsNullOrWhiteSpace(id) ? null : Phase3Validation.OptionalText(id, nameof(id), 128);
        Name = string.IsNullOrWhiteSpace(name) ? null : Phase3Validation.OptionalText(name, nameof(name), 64);
        if (argumentsDelta is { Length: > 4_096 })
        {
            throw new ArgumentException("Tool arguments delta exceeds 4096 characters.", nameof(argumentsDelta));
        }

        // Arguments fragments are kept verbatim: whitespace inside a delta can be
        // part of a JSON string literal, so trimming here would corrupt aggregation.
        ArgumentsDelta = argumentsDelta;
    }

    public int Index { get; }
    public string? Id { get; }
    public string? Name { get; }
    public string? ArgumentsDelta { get; }
}

public static class TodoToolContracts
{
    public const string SubmitTodoProposalsToolName = "submit_todo_proposals";

    public const string SubmitTodoProposalsParametersV1 =
        """
        {
          "type": "object",
          "properties": {
            "todos": {
              "type": "array",
              "minItems": 1,
              "maxItems": 10,
              "items": {
                "type": "object",
                "properties": {
                  "title":        { "type": "string", "maxLength": 500 },
                  "description":  { "type": "string", "maxLength": 4000 },
                  "priority":     { "enum": ["low", "normal", "high"] },
                  "due_at":       { "type": "string" }
                },
                "required": ["title"],
                "additionalProperties": false
              }
            }
          },
          "required": ["todos"],
          "additionalProperties": false
        }
        """;

    public const string SubmitTodoProposalsDescription =
        "仅当用户表达任务规划/待办意图时调用。提交的提案仅供用户在界面确认，确认前不创建任何待办；" +
        "不得在参数中包含路径、命令、workspace、task id 等执行字段。";

    public static ChatToolDefinition CreateSubmitTodoProposals() =>
        new(SubmitTodoProposalsToolName, SubmitTodoProposalsDescription, SubmitTodoProposalsParametersV1);
}
