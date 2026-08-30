using System.Globalization;
using FgoPet.Core.Todo;
using FgoPet.Core.Agents;

namespace FgoPet.App.ViewModels;

public sealed class TodoItemViewModel
{
    private readonly TimeProvider _time;

    public TodoItemViewModel(TodoItem item, TimeProvider time, AgentExecution? execution = null)
    {
        Item = item ?? throw new ArgumentNullException(nameof(item));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        Execution = execution is null ? null : new AgentExecutionViewModel(execution);
    }

    public TodoItem Item { get; }
    public AgentExecutionViewModel? Execution { get; }
    public string Id => Item.Id;
    public string Title => Item.Title;
    public string Description => Item.Description ?? string.Empty;
    public TodoStatus Status => Item.Status;
    public TodoPriority Priority => Item.Priority;
    public bool CanDispatch => Item.CanDispatch;
    public bool IsOverdue => Item.Status != TodoStatus.Completed
        && Item.DueAt is { } dueAt
        && dueAt <= _time.GetUtcNow();
    public string StatusText => Item.Status switch
    {
        TodoStatus.Planned when IsOverdue => "已逾期 · 等待派发",
        TodoStatus.Planned => "等待派发",
        TodoStatus.Active => "Agent 执行中",
        TodoStatus.Completed => "已完成",
        _ => "未知状态",
    };
    public string PriorityText => Item.Priority switch
    {
        TodoPriority.High => "高优先级",
        TodoPriority.Low => "低优先级",
        _ => "普通优先级",
    };
    public string DueText => Item.DueAt is { } dueAt
        ? dueAt.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.CurrentCulture)
        : "无截止时间";
    public string StatusAccent => Item.Status == TodoStatus.Active || IsOverdue ? "#FFD242E8" : "#FF70E7F5";
}
