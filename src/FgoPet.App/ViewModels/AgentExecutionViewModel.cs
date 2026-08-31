using FgoPet.Core.Agents;

namespace FgoPet.App.ViewModels;

public sealed class AgentExecutionViewModel
{
    public AgentExecutionViewModel(AgentExecution execution)
    {
        Execution = execution ?? throw new ArgumentNullException(nameof(execution));
    }

    public AgentExecution Execution { get; }
    public string TodoId => Execution.TodoId;
    public string SourceType => Execution.SourceType;
    public string StatusText => Execution.Status switch
    {
        AgentExecutionStatus.Dispatching => "正在派发",
        AgentExecutionStatus.Active => "Agent 执行中",
        AgentExecutionStatus.Attention => "需要你的确认",
        AgentExecutionStatus.Completed => "已完成",
        AgentExecutionStatus.Failed => "执行失败",
        AgentExecutionStatus.Cancelled => "已取消",
        _ => "未知状态",
    };
    public bool IsAttentionRequired => Execution.Status == AgentExecutionStatus.Attention;
}
