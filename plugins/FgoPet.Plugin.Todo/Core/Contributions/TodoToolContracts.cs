using FgoPet.Extensibility;
namespace FgoPet.Core.Dialogue;
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
                  "title":        { "type": "string", "minLength": 1, "maxLength": 500 },
                  "description":  { "type": "string", "maxLength": 4000 },
                  "priority":     { "enum": ["low", "normal", "high"] },
                  "due_at":       { "type": "string" },
                  "steps": {
                    "type": "array",
                    "maxItems": 20,
                    "items": {
                      "type": "object",
                      "properties": { "title": { "type": "string", "minLength": 1, "maxLength": 200 } },
                      "required": ["title"],
                      "additionalProperties": false
                    }
                  }
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
        "仅当用户表达任务规划/待办意图时调用。提交的提案只进入当前会话的待确认草稿，确认前不创建任何待办；" +
        "不得在参数中包含路径、命令、workspace、task id 等执行字段。";

    public static ToolDescriptor CreateSubmitTodoProposals() =>
        new(SubmitTodoProposalsToolName, SubmitTodoProposalsDescription, SubmitTodoProposalsParametersV1, ToolEffect.Proposal);
}
