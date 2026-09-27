using FgoPet.Extensibility;
namespace FgoPet.Plugin.Todo;

public sealed class TodoPromptProvider : IConversationPromptProvider
{
    private const string TodoToolUsage = "如需提交待办提案，请调用 submit_todo_proposals 工具。工具参数只进入当前会话的待确认草稿，确认前不创建任何待办、不派发任何 Agent；工具调用后只回复用户可读的自然语言草稿摘要，列出每个待办标题及其步骤，并明确说明尚未创建、可修改且等待用户确认。不要在正文展示工具名、schema、内部 ID 或原始 JSON。用户可以继续用自然语言修改，修改时提交包含父任务完整字段的全量新提案；只有用户明确确认后应用才写入 Todo。取消、含糊确认或无效提案不得写库。普通回复采用 JSON 对象，text 为用户可读正文，emotion 为 neutral、happy、excited、shy、concerned、sad、surprised 或 angry；不确定时使用 neutral。不使用 Markdown 代码围栏。一个共同目标默认生成一个待办；将执行或学习步骤写入 steps[].title，而不是 description；description 仅用于任务本身的说明，不写入确认流程话术、创建状态或其他临时话术；不要把确认流程话术写入 description。只有互不依赖的目标才拆成多个待办。";
    private const string TodoTextFallbackUsage = """
        当前请求没有可调用的工具；不要生成工具调用，不要声称已调用工具、创建待办或派发 Agent。
        普通回复仅返回 JSON 对象：{"text":"用户可读正文","emotion":"neutral"}。
        需要规划或修改待办时，返回文本提案 JSON：{"text":"列出每个草稿标题及步骤，说明尚未创建、可修改并等待确认。","emotion":"neutral","todos":[{"title":"共同目标","description":"任务说明","steps":[{"title":"执行或学习步骤"}]}]}。
        todos 为1至10项，每项只允许 title、description、priority、due_at、steps；title 必填，最多500字符；description 可省略，最多4000字符；priority 可省略，使用 normal、low 或 high；due_at 可省略，使用带时区的 ISO 8601 时间。每项 steps 最多20项，只含 title，每个步骤标题最多200字符。
        一个共同目标默认一个待办，步骤写入 steps[].title；仅互不依赖目标才拆成多个待办。修改时返回完整的新提案，不返回补丁。没有提案时省略 todos。
        提案只进入待确认草稿；只有用户明确确认后应用才写入 Todo，不派发 Agent。取消、含糊确认、无效提案不得声称已写库。description 不含确认流程、创建状态或临时话术。
        text 仅为用户可读正文，不展示 schema、工具名、内部 ID 或原始 JSON。不得提供命令、执行参数或本机路径。emotion 为 neutral、happy、excited、shy、concerned、sad、surprised 或 angry；不确定时用 neutral。不使用 Markdown 代码围栏。
        """;

    public IReadOnlyList<ConversationPromptBlock> BuildPrompt(ToolScope scope, string userMessage, bool toolsAvailable) =>
        [new("todo.policy", toolsAvailable ? TodoToolUsage : TodoTextFallbackUsage, ConversationPromptBlockKind.Instruction)];
}
