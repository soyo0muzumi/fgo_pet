using System.Security.Cryptography;
using System.Text.Json;
using FgoPet.Core.Todo;

namespace FgoPet.App.Services;

public sealed record TodoPeekTask(string Id, string Title, TodoStatus Status, DateTimeOffset? DueAt,
    int CompletedSteps, int TotalSteps, string Etag);

public sealed record TodoPeekSnapshot(long Revision, IReadOnlyList<TodoPeekTask> Tasks, int CompletedToday);

public sealed record TodoTaskDetail(string Id, string Title, string? Description, TodoPriority Priority,
    TodoStatus Status, DateTimeOffset? DueAt, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt, IReadOnlyList<TodoStep> Steps, string Etag, bool IsProtected);

public sealed record TodoWorkspaceSnapshot(long Revision, IReadOnlyList<TodoTaskDetail> Active,
    IReadOnlyList<TodoTaskDetail> Completed, IReadOnlyList<TodoTaskDetail> Today,
    IReadOnlyList<TodoTaskDetail> Inbox, IReadOnlyList<TodoTaskDetail> Upcoming,
    IReadOnlyList<TodoTaskDetail> All);

public static class TodoWorkspaceProjection
{
    public static (IReadOnlyList<TodoTaskDetail> Today, IReadOnlyList<TodoTaskDetail> Inbox,
        IReadOnlyList<TodoTaskDetail> Upcoming, IReadOnlyList<TodoTaskDetail> All)
        Project(IReadOnlyList<TodoTaskDetail> active, DateOnly today, TimeZoneInfo localZone)
    {
        ArgumentNullException.ThrowIfNull(active);
        ArgumentNullException.ThrowIfNull(localZone);
        var todayTasks = new List<TodoTaskDetail>();
        var inbox = new List<TodoTaskDetail>();
        var upcoming = new List<TodoTaskDetail>();
        foreach (var item in active)
        {
            if (item.Status == TodoStatus.Active) { todayTasks.Add(item); continue; }
            if (item.Status != TodoStatus.Planned) continue;
            if (item.DueAt is null) { inbox.Add(item); continue; }
            if (DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(item.DueAt.Value, localZone).DateTime) <= today)
                todayTasks.Add(item);
            else upcoming.Add(item);
        }
        return (todayTasks, inbox, upcoming, active.Where(item =>
            item.Status is TodoStatus.Planned or TodoStatus.Active).ToArray());
    }
}

/// <summary>Typed operations for Todo Web surfaces; all reads and writes use the existing application service.</summary>
public sealed class TodoWebAdapter(TodoApplicationService todos, TodoChangeFeed changes,
    TimeProvider? clock = null)
{
    public TodoWorkspaceSnapshot QueryTasks(CancellationToken cancellationToken = default)
    {
        var snapshot = changes.ReadStableSnapshot(() => (Active: todos.ListActive().Select(ToDetail).ToArray(),
            Completed: todos.ListHistory().Select(ToDetail).ToArray()), cancellationToken);
        var time = clock ?? TimeProvider.System;
        var projection = TodoWorkspaceProjection.Project(snapshot.Value.Active,
            DateOnly.FromDateTime(time.GetLocalNow().DateTime), time.LocalTimeZone);
        return new(snapshot.Revision, snapshot.Value.Active, snapshot.Value.Completed,
            projection.Today, projection.Inbox, projection.Upcoming, projection.All);
    }

    public TodoTaskDetail? GetTask(string id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return todos.Get(id) is { } item ? ToDetail(item) : null;
    }

    public TodoTaskDetail CreateTask(string title, string? description, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ToDetail(todos.Create(title, description, TodoPriority.Normal, null));
    }

    public TodoTaskDetail UpdateTask(string id, string etag, string title, string? description,
        IReadOnlyList<TodoStep>? steps = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var expected = RequireExpected(id, etag);
        return ToDetail(todos.Update(expected, title, description, steps));
    }

    public void DeleteTask(string id, string etag, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        todos.Delete(RequireExpected(id, etag));
    }

    public TodoTaskDetail UndoCompletion(TodoItem completed, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        todos.UndoCompletion(completed);
        return GetTask(completed.Id, cancellationToken)!;
    }

    private TodoItem RequireExpected(string id, string etag)
    {
        var item = todos.Get(id) ?? throw new KeyNotFoundException("TODO_NOT_FOUND");
        if (!string.Equals(Etag(item), etag, StringComparison.Ordinal))
            throw new InvalidOperationException("TODO_CONFLICT");
        return item;
    }

    private static TodoTaskDetail ToDetail(TodoItem item) => new(item.Id, item.Title, item.Description,
        item.Priority, item.Status, item.DueAt, item.CreatedAt, item.UpdatedAt, item.CompletedAt,
        item.Steps, Etag(item), item.Status == TodoStatus.Active);

    private static string Etag(TodoItem item) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(item)));

    public TodoPeekSnapshot GetPeekSnapshot(DateOnly today, TimeZoneInfo localZone,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(localZone);
        var snapshot = changes.ReadStableSnapshot(() => (Tasks: todos.ListActive()
            .Where(item => item.Status == TodoStatus.Active ||
                item.DueAt is null &&
                DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(item.CreatedAt, localZone).DateTime) == today ||
                item.DueAt is { } dueAt &&
                DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(dueAt, localZone).DateTime) <= today)
            .OrderByDescending(item => item.Status == TodoStatus.Active)
            .ThenBy(item => item.DueAt)
            .ThenBy(item => item.Title, StringComparer.CurrentCulture)
            .Select(item => new TodoPeekTask(item.Id, item.Title, item.Status, item.DueAt,
                item.Steps.Count(step => step.IsCompleted), item.Steps.Count, Etag(item)))
            .ToArray(), CompletedToday: todos.ListHistory().Count(item => item.CompletedAt is { } completedAt
                && DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(completedAt, localZone).DateTime) == today)),
            cancellationToken);
        return new TodoPeekSnapshot(snapshot.Revision, snapshot.Value.Tasks, snapshot.Value.CompletedToday);
    }

    public TodoItem QuickAdd(string title, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return todos.Create(title, null, TodoPriority.Normal, null);
    }

    public TodoItem SetCompletion(string id, bool completed, bool confirmIncompleteSteps = false,
        CancellationToken cancellationToken = default, string? etag = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (etag is not null)
        {
            var expected = RequireExpected(id, etag);
            return completed ? todos.Complete(expected, confirmIncompleteSteps) : todos.Reopen(expected);
        }
        return completed ? todos.Complete(id, confirmIncompleteSteps) : todos.Reopen(id);
    }
}
