using System.Text.Json;
using FgoPet.App.Archives;
using FgoPet.Core.Dialogue;
using FgoPet.App.Dialogue;
using FgoPet.App.Services;
using FgoPet.Core.Todo;
using FgoPet.Extensibility;
using Xunit;

namespace FgoPet.Plugin.Todo.Tests;

public sealed class TodoPluginTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Oversized_draft_is_rejected_before_replacing_a_readable_pending_draft(bool toolCall)
    {
        var repository = new Repository();
        var port = new TodoProposalService(new TodoApplicationService(repository, TimeProvider.System));
        var previous = port.Drafts.Replace("conversation", "role", [new TodoProposal("previous readable draft")]);
        await using var plugin = new TodoPlugin(port);
        await plugin.StartAsync(default);
        var arguments = JsonSerializer.SerializeToElement(new { todos = Enumerable.Range(0, 3)
            .Select(index => new { title = "task " + index, steps = Enumerable.Repeat(new { title = new string('x', 200) }, 20).ToArray() }).ToArray() });
        Assert.True(port.TryParseToolCall(arguments).Success);
        var scope = new ToolScope("conversation", "role", null);
        var result = toolCall ? (await plugin.InvokeAsync(new(scope, arguments), default)).Conversation
            : plugin.TryInterpretReply(scope, arguments.GetRawText());
        Assert.NotNull(result);
        Assert.Equal(CapabilityOutcome.InvalidToolCall, result.Outcome);
        Assert.Contains("分批", result.Reply);
        Assert.Same(previous, port.Drafts.Get("conversation", "role"));
        Assert.Equal(previous.DraftId, result.DraftId);
        Assert.Empty(repository.Items);
    }

    [Fact]
    public void Confirmation_contracts_and_archive_data_belong_to_the_pure_Todo_owner()
    {
        var todo = typeof(TodoItem).Assembly;
        Assert.All(new[] { typeof(ILegacyTodoProposalPort), typeof(ILegacyTodoProposalConfirmation) },
            type => Assert.Same(todo, type.Assembly));
        var core = typeof(ArchiveDraft).Assembly;
        Assert.Same(todo, core);
        Assert.Same(core, typeof(IArchiveDraftConfirmation).Assembly);
        Assert.DoesNotContain(core.GetReferencedAssemblies(), reference =>
            reference.Name is "FgoPet.Work.Todo" or "FgoPet.Work.Archives" or "FgoPet.Dialogue" or "PresentationFramework");
    }

    [Fact]
    public void Legacy_confirmation_writes_are_not_added_to_model_proposal_contracts()
    {
        var legacy = typeof(ILegacyTodoProposalConfirmation);
        foreach (var model in new[] { typeof(ITodoProposalReader), typeof(ITodoConversationPort) })
        {
            Assert.False(legacy.IsAssignableFrom(model));
            Assert.False(model.IsAssignableFrom(typeof(ILegacyTodoProposalPort)));
            var methods = model.GetInterfaces().Append(model).SelectMany(type => type.GetMethods());
            Assert.DoesNotContain(methods, method => method.Name is "Confirm" or "Create" or "Dispatch");
        }
        Assert.Equal("Confirm", Assert.Single(legacy.GetMethods()).Name);
        Assert.Empty(legacy.GetProperties());
        Assert.Contains(legacy, typeof(ILegacyTodoProposalPort).GetInterfaces());
        Assert.Equal("Parse", Assert.Single(typeof(ILegacyTodoProposalPort).GetMethods()).Name);
        var archive = Assert.Single(typeof(IArchiveDraftConfirmation).GetMethods());
        Assert.Equal("Confirm", archive.Name);
        Assert.Equal(typeof(void), archive.ReturnType);
        Assert.Equal(typeof(ArchiveDraft), Assert.Single(archive.GetParameters()).ParameterType);
        Assert.Empty(typeof(IArchiveDraftConfirmation).GetProperties());
    }

    private sealed class Repository : ITodoRepository
    {
        public List<TodoItem> Items { get; } = [];
        public void Save(TodoItem item) => Items.Add(item);
        public TodoItem? Get(string id) => Items.SingleOrDefault(item => item.Id == id);
        public IReadOnlyList<TodoItem> List(TodoStatus? status = null) => Items;
        public IReadOnlyList<TodoItem> ListCompletedOn(DateOnly date) => [];
        public void Delete(string id) { }
    }
}
