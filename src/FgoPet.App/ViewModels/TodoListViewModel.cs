using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FgoPet.App.Services;
using FgoPet.Core.Todo;

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

    public TodoListViewModel(TodoApplicationService service, TimeProvider time)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    public ObservableCollection<TodoItemViewModel> VisibleItems { get; } = new();
    public ObservableCollection<TodoGroupViewModel> Groups { get; } = new();

    [ObservableProperty]
    private TodoListTab _selectedTab = TodoListTab.Todo;

    [ObservableProperty]
    private bool _onlyToday;

    public bool HasOverflow { get; private set; }

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
        var visible = materialized.Take(MaxVisibleRows)
            .Select(item => new TodoItemViewModel(item, _time))
            .ToArray();
        foreach (var item in visible)
        {
            VisibleItems.Add(item);
        }

        Groups.Clear();
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
}
