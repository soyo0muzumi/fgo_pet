using FgoPet.Core.Todo;

namespace FgoPet.App.Dialogue;

public sealed record TodoProposal
{
    public TodoProposal(
        string title,
        string? description = null,
        TodoPriority priority = TodoPriority.Normal,
        DateTimeOffset? dueAt = null)
    {
        Title = ValidateText(title, nameof(title), 500);
        Description = string.IsNullOrWhiteSpace(description)
            ? null
            : ValidateText(description, nameof(description), 4_000);
        Priority = priority;
        DueAt = dueAt;
    }

    public string Title { get; }
    public string? Description { get; }
    public TodoPriority Priority { get; }
    public DateTimeOffset? DueAt { get; }

    private static string ValidateText(string value, string name, int maxLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        var normalized = value.Trim();
        if (normalized.Length > maxLength)
        {
            throw new ArgumentException($"{name} must be at most {maxLength} characters.", name);
        }

        return normalized;
    }
}
