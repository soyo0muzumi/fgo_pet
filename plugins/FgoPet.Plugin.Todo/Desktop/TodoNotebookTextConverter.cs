using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using FgoPet.Core.Todo;

namespace FgoPet.Plugin.Todo.Desktop;

/// <summary>Formats presentation text while the existing session remains the state owner.</summary>
public sealed class TodoNotebookTextConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        // The first binding observes the existing property notifications; the second
        // supplies typed state, so localization never parses labels or user titles.
        if (values.Length < 2) return string.Empty;
        return (parameter, values[1]) switch
        {
            ("progress", TodoEditorSession row) => row.HasSteps
                ? TodoWorkspaceStrings.Format("todo.progress", row.StepCount - row.IncompleteStepCount, row.StepCount) : string.Empty,
            ("task", TodoEditorSession row) => TodoWorkspaceStrings.Format(row.IsCompleted ? "todo.restore" : "todo.complete", row.Item.Title),
            ("step", TodoStepEditor step) => TodoWorkspaceStrings.Format(step.Item.IsCompleted ? "todo.stepComplete" : "todo.stepIncomplete", step.Item.Title),
            ("confirmation", TodoEditorSession row) => TodoWorkspaceStrings.Format("todo.completionPrompt", row.IncompleteStepCount),
            ("when", TodoEditorSession row) => WhenText(row.Item),
            ("whenColor", TodoEditorSession row) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(
                row.Item.Status != TodoStatus.Completed && row.Item.DueAt is { } due
                    ? due.LocalDateTime.Date < DateTime.Today ? "#A85460" : due.LocalDateTime.Date == DateTime.Today ? "#B2718B" : "#6E6961"
                    : "#6E6961")),
            _ => string.Empty
        };
    }

    private static string WhenText(TodoItem item)
    {
        if (item.Status == TodoStatus.Completed && item.CompletedAt is { } completed)
            return TodoWorkspaceStrings.Format("todo.completedOn", completed.LocalDateTime.ToString("MMM d", CultureInfo.GetCultureInfo("en-US")));
        if (item.DueAt is { } due)
        {
            if (due.LocalDateTime.Date < DateTime.Today) return TodoWorkspaceStrings.Get("todo.overdue");
            if (due.LocalDateTime.Date == DateTime.Today) return TodoWorkspaceStrings.Get("todo.dueToday");
            return TodoWorkspaceStrings.Format("todo.dueOn", due.LocalDateTime.ToString("MMM d", CultureInfo.GetCultureInfo("en-US")));
        }
        return item.CreatedAt.LocalDateTime.Date == DateTime.Today ? TodoWorkspaceStrings.Get("todo.addedToday")
            : TodoWorkspaceStrings.Format("todo.addedOn", item.CreatedAt.LocalDateTime.ToString("MMM d", CultureInfo.GetCultureInfo("en-US")));
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        targetTypes.Select(_ => Binding.DoNothing).ToArray();
}
