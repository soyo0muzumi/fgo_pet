using FgoPet.App.Dialogue;
using FgoPet.Core.Todo;
using Xunit;

namespace FgoPet.Core.Tests.Todo;

// This project references Core only. Merely compiling these tests must not require
// Work.Todo, Dialogue, a WPF runtime, or a database provider.
public sealed class TodoConversationContractTests
{
    [Fact]
    public void Proposal_and_draft_contracts_are_available_without_the_work_implementation()
    {
        var assembly = typeof(TodoItem).Assembly;
        var contracts = new[]
        {
            typeof(ITodoProposalReader), typeof(ITodoConversationPort), typeof(ITodoDraftWorkflow),
            typeof(TodoProposal), typeof(PendingTodoDraft), typeof(TodoDraftResult),
            typeof(TodoDraftStatus), typeof(TodoDraftResultKind),
            typeof(ToolCallProposalResult), typeof(TodoToolCallFailure),
        };
        Assert.All(contracts, type => Assert.Same(assembly, type.Assembly));
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference =>
            reference.Name is "FgoPet.Work.Todo" or "FgoPet.Dialogue" or "PresentationFramework");
    }

    [Fact]
    public void Read_port_cannot_create_todos_or_access_the_confirmation_workflow()
    {
        var methods = typeof(ITodoProposalReader).GetMethods().Select(method => method.Name).Order().ToArray();
        Assert.Equal(new[] { "BuildRuntimeState", "ParseEnvelope", "TryParseToolCall" }, methods);
        Assert.Empty(typeof(ITodoProposalReader).GetProperties());
        Assert.Contains(typeof(ITodoProposalReader), typeof(ITodoConversationPort).GetInterfaces());
        var property = Assert.Single(typeof(ITodoConversationPort).GetProperties());
        Assert.Equal("Drafts", property.Name);
        Assert.Equal(typeof(ITodoDraftWorkflow), property.PropertyType);
        Assert.False(property.CanWrite);
        Assert.DoesNotContain(typeof(ITodoConversationPort).GetMethods(), method => method.Name is "Confirm" or "Create" or "Dispatch");
    }

    [Fact]
    public void Proposal_snapshot_and_failure_result_remain_contract_only_values()
    {
        var steps = new[] { "查看笔记", "整理目录" };
        var proposal = new TodoProposal("整理资料", "只整理，不执行", stepTitles: steps);
        steps[0] = "不应修改已有快照";
        Assert.Equal("查看笔记", proposal.StepTitles[0]);
        var result = ToolCallProposalResult.Ok([proposal]);
        Assert.True(result.Success);
        Assert.Same(proposal, Assert.Single(result.Proposals!));
        var failure = ToolCallProposalResult.Fail(TodoToolCallFailure.UnsupportedField, "command");
        Assert.False(failure.Success);
        Assert.Null(failure.Proposals);
        Assert.Equal("command", failure.FieldName);
    }
}
