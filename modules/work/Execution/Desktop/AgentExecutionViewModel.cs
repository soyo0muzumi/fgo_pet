using FgoPet.Core.Agents;

namespace FgoPet.App.ViewModels;

public sealed class AgentExecutionViewModel
{
    public AgentExecutionViewModel(AgentExecution execution)
    {
        Execution = execution ?? throw new ArgumentNullException(nameof(execution));
    }

    public AgentExecution Execution { get; }
    public string ExecutionId => Execution.Id;
    public string TodoId => Execution.TodoId;
    public string SourceType => Execution.SourceType;
    public string SourceInstance => Execution.SourceInstance;
    public string TaskId => Execution.TaskId;
    public string DispatchRequestId => Execution.DispatchRequestId;
    public bool IsOutcomeUnknown => Execution.Status == AgentExecutionStatus.DispatchOutcomeUnknown;
    public bool CanRetryDispatch => false;
    public string DiagnosticBlock =>
        $"来源类型：{SourceType}\n来源实例：{SourceInstance}\n任务 ID：{TaskId}\n派发请求 ID：{DispatchRequestId}\n执行时间：{Execution.UpdatedAt:O}";
    public string StatusText => Execution.Status switch
    {
        AgentExecutionStatus.Dispatching => "正在派发",
        AgentExecutionStatus.Active => "Agent 执行中",
        AgentExecutionStatus.Attention => "需要你的确认",
        AgentExecutionStatus.DispatchOutcomeUnknown => "待核对",
        AgentExecutionStatus.Completed => "已完成",
        AgentExecutionStatus.Failed => "执行失败",
        AgentExecutionStatus.Cancelled => "已取消",
        _ => "未知状态",
    };
    public bool IsAttentionRequired => Execution.Status == AgentExecutionStatus.Attention;
}
