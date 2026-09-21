namespace FgoPet.Core.Todo;

/// <summary>
/// An immutable, single-level checklist step owned by a <see cref="TodoItem"/>.
/// </summary>
public sealed record TodoStep
{
    public TodoStep(string id, string title, int order, bool isCompleted = false)
    {
        Id = TodoValidation.Id(id, nameof(id));
        Title = TodoValidation.Text(title, nameof(title), TodoValidation.MaxStepTitleLength);

        if (order is < 0 or >= TodoValidation.MaxStepCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(order),
                order,
                $"{nameof(order)} must be between 0 and {TodoValidation.MaxStepCount - 1}.");
        }

        Order = order;
        IsCompleted = isCompleted;
    }

    public string Id { get; }
    public string Title { get; }
    public int Order { get; }
    public bool IsCompleted { get; }
}
