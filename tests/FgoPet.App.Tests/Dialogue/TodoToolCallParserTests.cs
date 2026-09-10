using System.Text.Json;
using FgoPet.App.Dialogue;
using FgoPet.App.Services;
using FgoPet.Core.Todo;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

public sealed class TodoToolCallParserTests
{
    private static TodoProposalService CreateService() =>
        new(new TodoApplicationService(new FakeTodoRepository(), TimeProvider.System));

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    [Fact]
    public void Valid_proposals_parse_successfully()
    {
        var arguments = Parse("""
            {"todos":[{"title":"写回归测试","description":"补 tool call 用例","priority":"high","due_at":"2026-09-05"}]}
            """);

        var result = CreateService().TryParseToolCall(arguments);

        Assert.True(result.Success);
        var proposal = Assert.Single(result.Proposals!);
        Assert.Equal("写回归测试", proposal.Title);
        Assert.Equal(TodoPriority.High, proposal.Priority);
    }

    [Fact]
    public void Missing_todos_array_fails()
    {
        var result = CreateService().TryParseToolCall(Parse("{}"));

        Assert.False(result.Success);
        Assert.Equal(TodoToolCallFailure.MissingTodos, result.Failure);
    }

    [Fact]
    public void Empty_todos_array_fails()
    {
        var result = CreateService().TryParseToolCall(Parse("""{"todos":[]}"""));

        Assert.False(result.Success);
        Assert.Equal(TodoToolCallFailure.MissingTodos, result.Failure);
    }

    [Fact]
    public void More_than_ten_proposals_fail()
    {
        var items = string.Join(",", Enumerable.Range(1, 11).Select(i => $$"""{"title":"任务 {{i}}"}"""));
        var result = CreateService().TryParseToolCall(Parse($$"""{"todos":[{{items}}]}"""));

        Assert.False(result.Success);
        Assert.Equal(TodoToolCallFailure.TooMany, result.Failure);
    }

    [Fact]
    public void Unknown_top_level_field_fails_with_field_name()
    {
        var result = CreateService().TryParseToolCall(
            Parse("""{"todos":[{"title":"x"}],"workspace":"D:/tmp"}"""));

        Assert.False(result.Success);
        Assert.Equal(TodoToolCallFailure.UnsupportedField, result.Failure);
        Assert.Equal("workspace", result.FieldName);
    }

    [Fact]
    public void Denied_execution_field_inside_proposal_fails()
    {
        var result = CreateService().TryParseToolCall(
            Parse("""{"todos":[{"title":"x","command":"rm -rf /"}]}"""));

        Assert.False(result.Success);
        Assert.Equal(TodoToolCallFailure.UnsupportedField, result.Failure);
        Assert.Equal("command", result.FieldName);
    }

    [Fact]
    public void Unsafe_path_in_title_fails()
    {
        var result = CreateService().TryParseToolCall(
            Parse("""{"todos":[{"title":"打开 D:\\secret\\file.txt"}]}"""));

        Assert.False(result.Success);
        Assert.Equal(TodoToolCallFailure.NotPlanning, result.Failure);
    }

    [Fact]
    public void Non_object_arguments_fail()
    {
        var result = CreateService().TryParseToolCall(Parse("[1,2]"));

        Assert.False(result.Success);
        Assert.Equal(TodoToolCallFailure.InvalidJson, result.Failure);
    }

    private sealed class FakeTodoRepository : ITodoRepository
    {
        public void Save(TodoItem todo) { }
        public TodoItem? Get(string id) => null;
        public IReadOnlyList<TodoItem> List(TodoStatus? status = null) => [];
        public IReadOnlyList<TodoItem> ListCompletedOn(DateOnly localDate) => [];
        public void Delete(string id) { }
        public void ClearAgentTodoData() { }
    }
}
