using FgoPet.Core.Todo;

namespace FgoPet.App.Dialogue;

public sealed record TodoProposal
{
    public TodoProposal(
        string title,
        string? description = null,
        TodoPriority priority = TodoPriority.Normal,
        DateTimeOffset? dueAt = null,
        IReadOnlyList<string>? stepTitles = null)
    {
        Title = ValidateText(title, nameof(title), 500);
        Description = string.IsNullOrWhiteSpace(description)
            ? null
            : ValidateText(description, nameof(description), 4_000);
        Priority = priority;
        DueAt = dueAt;
        StepTitles = Array.AsReadOnly((stepTitles ?? Array.Empty<string>())
            .Select(step => ValidateText(step, "step title", 200))
            .ToArray());
        if (StepTitles.Count > 20)
        {
            throw new ArgumentException("A Todo proposal may contain at most 20 steps.", nameof(stepTitles));
        }
    }

    public string Title { get; }
    public string? Description { get; }
    public TodoPriority Priority { get; }
    public DateTimeOffset? DueAt { get; }
    public IReadOnlyList<string> StepTitles { get; }

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
