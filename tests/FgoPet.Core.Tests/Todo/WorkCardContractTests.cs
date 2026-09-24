using FgoPet.App.Archives;
using FgoPet.App.Dialogue;
using FgoPet.Core.Todo;
using Xunit;

namespace FgoPet.Core.Tests.Todo;

// This project references Core only: no Work implementation, WPF or database is needed.
public sealed class WorkCardContractTests
{
    [Fact]
    public void Card_contracts_and_archive_data_are_available_from_Core_alone()
    {
        var core = typeof(TodoItem).Assembly;
        Assert.All(new[] { typeof(ILegacyTodoProposalPort), typeof(ILegacyTodoProposalConfirmation),
            typeof(IArchiveDraftConfirmation), typeof(ArchiveDraft) }, type => Assert.Same(core, type.Assembly));
        Assert.DoesNotContain(core.GetReferencedAssemblies(), reference =>
            reference.Name is "FgoPet.Work.Todo" or "FgoPet.Work.Archives" or "FgoPet.Dialogue" or "PresentationFramework");
    }

    [Fact]
    public void Legacy_card_writes_are_not_added_to_model_proposal_contracts()
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

    [Fact]
    public void Archive_contract_preserves_existing_record_copy_semantics_and_provenance()
    {
        var original = new ArchiveDraft("archive-fixture", "fixture", ["todo-a", "todo-b"],
            new(2026, 1, 3), "Original", new(2026, 1, 1), new(2026, 1, 2), "Summary",
            ["Outcome"], "Synthetic model input");
        var edited = original with { Title = "Edited", Summary = "Edited summary" };
        Assert.Equal(2, edited.CoveredTodoCount);
        Assert.Equal(original.ArchiveId, edited.ArchiveId);
        Assert.Equal(original.SourceType, edited.SourceType);
        Assert.Same(original.CoveredTodoKeys, edited.CoveredTodoKeys);
        Assert.Same(original.Outcomes, edited.Outcomes);
        Assert.Equal(original.ModelInput, edited.ModelInput);
        Assert.Equal(original.ArchiveDate, edited.ArchiveDate);
        Assert.Equal(original.StartedOn, edited.StartedOn);
        Assert.Equal(original.CompletedOn, edited.CompletedOn);
        Assert.Equal("Original", original.Title);
        Assert.Equal("Summary", original.Summary);
    }
}
