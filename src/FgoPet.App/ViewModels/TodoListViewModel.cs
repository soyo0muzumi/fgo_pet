using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FgoPet.App.Services;
using FgoPet.Core.Agents;
using FgoPet.Core.Todo;
using FgoPet.Core.Archives;
using FgoPet.Infrastructure.Agents;

namespace FgoPet.App.ViewModels;

public enum TodoListTab
{
    Todo,
    History,
}

public sealed class TodoGroupViewModel
{
    public TodoGroupViewModel(string header, IReadOnlyList<TodoItemViewModel> items)
    {
        Header = header;
        Items = items;
    }

    public string Header { get; }
    public IReadOnlyList<TodoItemViewModel> Items { get; }
}

public sealed partial class TodoListViewModel : ObservableObject
{
    public const int MaxVisibleRows = 8;

    private readonly TodoApplicationService _service;
    private readonly TimeProvider _time;
    private readonly IWorkArchiveRepository? _archives;
    private readonly IAgentRelayAdministration? _administration;
    private readonly AgentDispatchService? _dispatchService;
    private readonly AgentEventProjector? _projector;
    private readonly IAgentRepository? _agents;

    public TodoListViewModel(
        TodoApplicationService service,
        TimeProvider time,
        IWorkArchiveRepository? archives = null,
        IAgentRelayAdministration? administration = null,
        AgentDispatchService? dispatchService = null,
        AgentEventProjector? projector = null,
        IAgentRepository? agents = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _archives = archives;
        _administration = administration;
        _dispatchService = dispatchService;
        _projector = projector;
        _agents = agents;
        if (_projector is not null)
        {
            _projector.EventApplied += OnAgentEventApplied;
        }
    }

    public ObservableCollection<TodoItemViewModel> VisibleItems { get; } = new();
    public ObservableCollection<TodoGroupViewModel> Groups { get; } = new();
    public ObservableCollection<WorkArchive> WorkArchives { get; } = new();

    /// <summary>
    /// Raised only after the user explicitly presses a Todo's “交给 Agent” button.
    /// The panel view hosts the resulting modal dialog; no dispatch happens here.
    /// </summary>
    public event Action<AgentDispatchDialogViewModel>? DispatchRequested;

    [ObservableProperty]
    private TodoListTab _selectedTab = TodoListTab.Todo;

    [ObservableProperty]
    private bool _onlyToday;

    public bool HasOverflow { get; private set; }

    public bool CanOpenDispatch => _administration is not null && _dispatchService is not null;

    public void RequestDispatch(TodoItem todo)
    {
        ArgumentNullException.ThrowIfNull(todo);
        if (!todo.CanDispatch || _administration is null || _dispatchService is null)
        {
            return;
        }

        DispatchRequested?.Invoke(new AgentDispatchDialogViewModel(todo, _administration, _dispatchService));
    }

    public void SelectTab(TodoListTab tab)
    {
        SelectedTab = tab;
        Refresh();
    }

    public void Refresh()
    {
        IEnumerable<TodoItem> items = SelectedTab == TodoListTab.Todo
            ? _service.ListActive()
                .OrderBy(item => item.Status == TodoStatus.Active ? 0 : 1)
                .ThenByDescending(item => item.Priority)
                .ThenBy(item => item.DueAt ?? DateTimeOffset.MaxValue)
                .ThenBy(item => item.CreatedAt)
            : OnlyToday
                ? _service.ListHistoryOn(DateOnly.FromDateTime(_time.GetLocalNow().DateTime.Date))
                : _service.ListHistory();

        var materialized = items.ToArray();
        HasOverflow = materialized.Length > MaxVisibleRows;
        VisibleItems.Clear();
        var visible = materialized
            .Select(item => new TodoItemViewModel(item, _time, _agents?.GetLatestExecutionForTodo(item.Id)))
            .ToArray();
        foreach (var item in visible)
        {
            VisibleItems.Add(item);
        }

        Groups.Clear();
        WorkArchives.Clear();
        if (SelectedTab == TodoListTab.History)
        {
            foreach (var group in visible.GroupBy(item => GetHistoryHeader(item.Item.CompletedAt ?? item.Item.UpdatedAt)))
            {
                Groups.Add(new TodoGroupViewModel(group.Key, group.ToArray()));
            }
        }
        else if (visible.Length > 0)
        {
            Groups.Add(new TodoGroupViewModel("待办事项", visible));
        }

        if (SelectedTab == TodoListTab.History && _archives is not null)
        {
            foreach (var archive in _archives.List())
            {
                WorkArchives.Add(archive);
            }
        }

        OnPropertyChanged(nameof(HasOverflow));
    }

    private string GetHistoryHeader(DateTimeOffset timestamp)
    {
        var localDate = timestamp.ToLocalTime().Date;
        var today = _time.GetLocalNow().Date;
        return localDate == today
            ? "今天"
            : localDate == today.AddDays(-1)
                ? "昨天"
                : localDate.ToString("yyyy-MM-dd");
    }

    partial void OnSelectedTabChanged(TodoListTab value) => Refresh();

    partial void OnOnlyTodayChanged(bool value)
    {
        if (SelectedTab == TodoListTab.History)
        {
            Refresh();
        }
    }

    private void OnAgentEventApplied(AgentEvent agentEvent, AgentProjectionApplyResult result)
    {
        if (result == AgentProjectionApplyResult.Applied)
        {
            // Agent event persistence updates the Todo in the same transaction.
            // Rebuild the item view models so an open panel reflects completion,
            // failure, cancellation, and active progress immediately.
            Refresh();
        }
    }
}
