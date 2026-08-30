namespace FgoPet.Core.Todo;

public sealed record TodoItem
{
    public TodoItem(
        string id,
        string title,
        string? description,
        TodoPriority priority,
        DateTimeOffset? dueAt,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        TodoStatus status = TodoStatus.Planned,
        DateTimeOffset? completedAt = null)
    {
        Id = TodoValidation.Id(id, nameof(id));
        Title = TodoValidation.Text(title, nameof(title), 500);
        Description = TodoValidation.OptionalText(description, nameof(description), 4_000);
        Priority = priority;
        DueAt = dueAt;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
        Status = status;
        CompletedAt = completedAt;

        if (status == TodoStatus.Completed && completedAt is null)
        {
            throw new ArgumentException("Completed Todo items require completedAt.", nameof(completedAt));
        }

        if (status != TodoStatus.Completed && completedAt is not null)
        {
            throw new ArgumentException("Only completed Todo items can have completedAt.", nameof(completedAt));
        }
    }

    public TodoItem(
        string id,
        string title,
        string? description,
        TodoPriority priority,
        DateTimeOffset? dueAt,
        TodoStatus status,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        DateTimeOffset? completedAt = null)
        : this(id, title, description, priority, dueAt, createdAt, updatedAt, status, completedAt)
    {
    }

    public string Id { get; }
    public string Title { get; }
    public string? Description { get; }
    public TodoPriority Priority { get; }
    public DateTimeOffset? DueAt { get; }
    public TodoStatus Status { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public bool CanDispatch => Status == TodoStatus.Planned;

    public TodoItem Activate(DateTimeOffset at)
    {
        if (Status != TodoStatus.Planned)
        {
            throw new InvalidOperationException("Only planned Todo items can become active.");
        }

        return this with { Status = TodoStatus.Active, UpdatedAt = at };
    }

    public TodoItem Complete(DateTimeOffset at)
    {
        if (Status == TodoStatus.Completed)
        {
            throw new InvalidOperationException("A completed Todo item cannot be completed again.");
        }

        return this with
        {
            Status = TodoStatus.Completed,
            UpdatedAt = at,
            CompletedAt = at,
        };
    }

    public TodoItem ReturnToPlanned(DateTimeOffset at)
    {
        if (Status == TodoStatus.Completed)
        {
            throw new InvalidOperationException("A completed Todo item cannot be replanned.");
        }

        return this with
        {
            Status = TodoStatus.Planned,
            UpdatedAt = at,
            CompletedAt = null,
        };
    }
}

internal static class TodoValidation
{
    public static string Id(string value, string parameterName, int maxLength = 128)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim();
        if (normalized.Length > maxLength)
        {
            throw new ArgumentException($"{parameterName} must be at most {maxLength} characters.", parameterName);
        }

        return normalized;
    }

    public static string Text(string value, string parameterName, int maxLength)
    {
        return Id(value, parameterName, maxLength);
    }

    public static string? OptionalText(string? value, string parameterName, int maxLength)
    {
        return string.IsNullOrWhiteSpace(value) ? null : Text(value, parameterName, maxLength);
    }
}
