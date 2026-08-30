using System.Text.Json;
using System.Text.RegularExpressions;
using FgoPet.App.Services;
using FgoPet.Core.Todo;

namespace FgoPet.App.Dialogue;

/// <summary>Parses bounded model proposals and writes them only after explicit confirmation.</summary>
public sealed class TodoProposalService
{
    private static readonly Regex AbsolutePath = new(
        @"[A-Za-z]:[\\/][^\s,;]+|\\\\[^\s,;]+|(?<!\w)/(?:Users|home|workspace|tmp)/[^\s,;]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> DeniedFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "source_task_id", "sourceTaskId", "workspace", "workspace_id", "working_directory",
        "path", "local_path", "command", "prompt", "reasoning", "tool_call", "tool_arguments",
        "execution", "agent_target", "target_id",
    };

    private readonly TodoApplicationService _todos;

    public TodoProposalService(TodoApplicationService todos)
    {
        _todos = todos ?? throw new ArgumentNullException(nameof(todos));
    }

    public IReadOnlyList<TodoProposal> Parse(string modelResponse)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelResponse);
        try
        {
            using var document = JsonDocument.Parse(modelResponse, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });
            var root = document.RootElement;
            var values = root.ValueKind switch
            {
                JsonValueKind.Array => root.EnumerateArray().ToArray(),
                JsonValueKind.Object when TryGetArray(root, "todos", out var todos) => todos,
                JsonValueKind.Object when TryGetArray(root, "proposals", out var proposals) => proposals,
                JsonValueKind.Object => new[] { root },
                _ => throw new FormatException("Todo proposals must be a JSON object or array."),
            };

            if (values.Length == 0 || values.Length > 10)
            {
                throw new FormatException("A Todo proposal response must contain between 1 and 10 items.");
            }

            return values.Select(ParseOne).ToArray();
        }
        catch (JsonException error)
        {
            throw new FormatException("Todo proposal JSON is invalid.", error);
        }
    }

    public TodoItem Confirm(TodoProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        return _todos.Create(proposal.Title, proposal.Description, proposal.Priority, proposal.DueAt);
    }

    public IReadOnlyList<string> BuildModelContext(string userMessage)
    {
        var words = (userMessage ?? string.Empty)
            .Split(new[] { ' ', '\t', '\r', '\n', ',', '，', '。', '、' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _todos.ListActive()
            .OrderByDescending(item => words.Count(word => item.Title.Contains(word, StringComparison.OrdinalIgnoreCase)))
            .ThenByDescending(item => item.UpdatedAt)
            .Take(10)
            .Select(item => $"- {StatusLabel(item.Status)}：{Redact(item.Title)}{FormatDescription(item.Description)}")
            .ToArray();
    }

    private static TodoProposal ParseOne(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("Each Todo proposal must be an object.");
        }

        foreach (var property in value.EnumerateObject())
        {
            if (DeniedFields.Contains(property.Name))
            {
                throw new FormatException($"Todo proposals cannot contain '{property.Name}'.");
            }
        }

        var title = ReadString(value, "title");
        if (string.IsNullOrWhiteSpace(title) || LooksUnsafe(title))
        {
            throw new FormatException("Todo proposal title is missing or unsafe.");
        }

        var description = ReadString(value, "description");
        if (description is not null && LooksUnsafe(description))
        {
            throw new FormatException("Todo proposal description is unsafe.");
        }

        var priority = TodoPriority.Normal;
        var priorityText = ReadString(value, "priority");
        if (!string.IsNullOrWhiteSpace(priorityText) && !Enum.TryParse(priorityText, true, out priority))
        {
            throw new FormatException("Todo proposal priority is invalid.");
        }

        DateTimeOffset? dueAt = null;
        var dueText = ReadString(value, "due_at") ?? ReadString(value, "dueAt");
        if (!string.IsNullOrWhiteSpace(dueText))
        {
            if (!DateTimeOffset.TryParse(dueText, out var parsed))
            {
                throw new FormatException("Todo proposal due_at is invalid.");
            }

            dueAt = parsed;
        }

        return new TodoProposal(title, description, priority, dueAt);
    }

    private static bool TryGetArray(JsonElement value, string name, out JsonElement[] items)
    {
        if (value.TryGetProperty(name, out var child) && child.ValueKind == JsonValueKind.Array)
        {
            items = child.EnumerateArray().ToArray();
            return true;
        }

        items = [];
        return false;
    }

    private static string? ReadString(JsonElement value, string name) =>
        value.TryGetProperty(name, out var child) && child.ValueKind == JsonValueKind.String
            ? child.GetString()?.Trim()
            : null;

    private static bool LooksUnsafe(string value) => AbsolutePath.IsMatch(value);

    private static string Redact(string value) => AbsolutePath.Replace(value, "[路径已隐藏]");

    private static string FormatDescription(string? value) => string.IsNullOrWhiteSpace(value)
        ? string.Empty
        : $" — {Redact(value)}";

    private static string StatusLabel(TodoStatus status) => status switch
    {
        TodoStatus.Active => "执行中",
        _ => "待办",
    };
}
