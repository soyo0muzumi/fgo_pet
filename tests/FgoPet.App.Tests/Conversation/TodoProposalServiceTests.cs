using FgoPet.App.Services;
using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Todo;
using Xunit;

namespace FgoPet.App.Tests.Conversation;

public sealed class TodoProposalServiceTests
{
    [Fact]
    public void Parses_only_the_supported_structured_proposal_fields()
    {
        var repository = new FakeTodoRepository();
        var service = new TodoProposalService(new TodoApplicationService(repository, TimeProvider.System));

        var proposals = service.Parse("""{"todos":[{"title":"Ship tests","description":"Write the regression tests","priority":"high","due_at":"2026-09-01T09:00:00+08:00"}]}""");

        var proposal = Assert.Single(proposals);
        Assert.Equal("Ship tests", proposal.Title);
        Assert.Equal(TodoPriority.High, proposal.Priority);
        Assert.Equal("Write the regression tests", proposal.Description);
        Assert.Empty(repository.Items);
    }

    [Fact]
    public void Rejects_execution_and_workspace_fields_from_model_output()
    {
        var service = new TodoProposalService(new TodoApplicationService(new FakeTodoRepository(), TimeProvider.System));

        Assert.Throws<FormatException>(() => service.Parse("""{"todos":[{"title":"Inspect","workspace":"C:\\secret"}]}"""));
    }

    [Fact]
    public void ParseEnvelope_ignores_plain_chat_but_extracts_todos()
    {
        var service = new TodoProposalService(new TodoApplicationService(new FakeTodoRepository(), TimeProvider.System));

        Assert.Null(service.ParseEnvelope("今天状态不错。"));
        var proposals = service.ParseEnvelope("""{"text":"我整理一下。","todos":[{"title":"整理测试"}]}""");

        Assert.NotNull(proposals);
        Assert.Equal("整理测试", Assert.Single(proposals!).Title);
    }

    [Fact]
    public void Rejects_unknown_proposal_fields_in_the_envelope()
    {
        var service = new TodoProposalService(new TodoApplicationService(new FakeTodoRepository(), TimeProvider.System));

        Assert.Throws<FormatException>(() => service.ParseEnvelope("""{"todos":[{"title":"Inspect","metadata":"ignored"}]}"""));
    }

    [Fact]
    public void Builds_at_most_ten_redacted_todo_context_items_without_ids_or_paths()
    {
        var repository = new FakeTodoRepository();
        for (var index = 0; index < 12; index++)
        {
            repository.Items.Add(new TodoItem($"internal-{index}", $"Task {index}", $"Use C:\\repo\\file{index}.cs", TodoPriority.Normal, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        }

        var service = new TodoProposalService(new TodoApplicationService(repository, TimeProvider.System));
        var context = service.BuildModelContext("Task");

        Assert.Equal(10, context.Count);
        Assert.DoesNotContain(context, item => item.Contains("internal-", StringComparison.Ordinal));
        Assert.DoesNotContain(context, item => item.Contains("C:\\repo", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildRuntimeState_is_bounded_to_the_prompt_contract()
    {
        var repository = new FakeTodoRepository();
        repository.Items.Add(new TodoItem("id", new string('标', 200), new string('描', 200), TodoPriority.High, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        var service = new TodoProposalService(new TodoApplicationService(repository, TimeProvider.System));

        var context = service.BuildRuntimeState("标题");

        Assert.True(context.Length <= PromptContracts.MaxRuntimeStateChars);
        Assert.Contains("[已截断]", context);
    }

    private sealed class FakeTodoRepository : ITodoRepository
    {
        public List<TodoItem> Items { get; } = new();
        public void Save(TodoItem todo) { Items.RemoveAll(item => item.Id == todo.Id); Items.Add(todo); }
        public TodoItem? Get(string id) => Items.SingleOrDefault(item => item.Id == id);
        public IReadOnlyList<TodoItem> List(TodoStatus? status = null) => status is null ? Items.ToArray() : Items.Where(item => item.Status == status).ToArray();
        public IReadOnlyList<TodoItem> ListCompletedOn(DateOnly localDate) => Array.Empty<TodoItem>();
        public void Delete(string id) => Items.RemoveAll(item => item.Id == id);
        public void ClearAgentTodoData() => Items.Clear();
    }
}
