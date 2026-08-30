using FgoPet.Core.Archives;
using FgoPet.Core.Todo;

namespace FgoPet.App.Archives;

public sealed record ArchiveDraft(
    string ArchiveId,
    string SourceType,
    IReadOnlyList<string> CoveredTodoKeys,
    DateOnly ArchiveDate,
    string Title,
    DateOnly? StartedOn,
    DateOnly? CompletedOn,
    string Summary,
    IReadOnlyList<string> Outcomes,
    string ModelInput)
{
    public int CoveredTodoCount => CoveredTodoKeys.Count;
}

public sealed class ArchiveDraftService
{
    private readonly ITodoRepository _todos;
    private readonly IWorkArchiveRepository _archives;
    private readonly TimeProvider _time;

    public ArchiveDraftService(ITodoRepository todos, IWorkArchiveRepository archives, TimeProvider time)
    {
        _todos = todos ?? throw new ArgumentNullException(nameof(todos));
        _archives = archives ?? throw new ArgumentNullException(nameof(archives));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    public ArchiveDraft CreateDraft(string sourceType, IReadOnlyList<TodoItem> coveredTodos, string summary)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceType);
        ArgumentNullException.ThrowIfNull(coveredTodos);
        ArgumentException.ThrowIfNullOrWhiteSpace(summary);
        if (coveredTodos.Count == 0)
        {
            throw new ArgumentException("At least one completed Todo is required.", nameof(coveredTodos));
        }

        if (coveredTodos.Any(todo => todo.Status != TodoStatus.Completed || _todos.Get(todo.Id)?.Status != TodoStatus.Completed))
        {
            throw new InvalidOperationException("Only completed Todo items can be archived.");
        }

        var now = _time.GetLocalNow();
        var localDate = DateOnly.FromDateTime(now.DateTime.Date);
        var input = string.Join(
            Environment.NewLine,
            coveredTodos.Select(todo => $"- {todo.Title}{(string.IsNullOrWhiteSpace(todo.Description) ? string.Empty : $"：{todo.Description}")}"));
        return new ArchiveDraft(
            "archive-" + Guid.NewGuid().ToString("N"),
            sourceType.Trim(),
            coveredTodos.Select(todo => todo.Id).Distinct(StringComparer.Ordinal).ToArray(),
            localDate,
            "工作归档",
            coveredTodos.Min(todo => todo.CompletedAt)?.ToLocalTime() is { } first ? DateOnly.FromDateTime(first.DateTime.Date) : null,
            coveredTodos.Max(todo => todo.CompletedAt)?.ToLocalTime() is { } last ? DateOnly.FromDateTime(last.DateTime.Date) : null,
            summary.Trim(),
            Array.Empty<string>(),
            input);
    }

    public void Confirm(ArchiveDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var text = string.IsNullOrWhiteSpace(draft.Title)
            ? draft.Summary
            : $"{draft.Title}\n{draft.Summary}";
        _archives.Confirm(new WorkArchive(
            draft.ArchiveId,
            draft.CoveredTodoKeys,
            new[] { draft.SourceType },
            draft.ArchiveDate,
            text,
            _time.GetUtcNow()));
    }
}
