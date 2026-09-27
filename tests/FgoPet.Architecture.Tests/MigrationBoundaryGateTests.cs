using System.Collections.Immutable;
using Xunit;

namespace FgoPet.Architecture.Tests;

public sealed class MigrationBoundaryGateTests
{
    private static readonly ArchitecturePolicy Policy = new(1,
    [new("src/Core/Core.csproj", "legacy-core", ArchitectureLayer.LegacyMixed, ArchitectureRole.Production),
     new("src/Kernel/Kernel.csproj", "kernel", ArchitectureLayer.Application, ArchitectureRole.Production),
     new("plugins/Todo/Todo.csproj", "todo", ArchitectureLayer.Desktop, ArchitectureRole.Production)],
    [new("kernel-to-contract", ["kernel"], [ArchitectureLayer.Application], ["todo"], [ArchitectureLayer.Contract], [ArchitectureDependencyKind.Compile])]);

    [Fact]
    public void Existing_debt_is_allowed_and_removal_is_accepted()
    {
        var debt = Debt();
        Assert.Empty(MigrationBoundaryGate.Verify(Policy, debt, debt, [Core(link: "modules/Todo.cs"), Kernel("plugins/Todo/Todo.csproj"), Todo()]));
        Assert.Empty(MigrationBoundaryGate.Verify(Policy, MigrationBaseline.Empty, debt, [Core(), Kernel(), Todo()]));
    }

    [Fact]
    public void New_legacy_compile_item_is_rejected()
    {
        Assert.Contains(MigrationBoundaryGate.Verify(Policy, Debt(), Debt(), [Core(link: "modules/New.cs"), Kernel(), Todo()]),
            error => error.Contains("legacy compile", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("kernel")]
    [InlineData("todo")]
    public void New_forbidden_dependency_is_rejected(string origin)
    {
        var snapshots = origin == "kernel"
            ? new[] { Core(), Kernel("plugins/Todo/Todo.csproj"), Todo() }
            : new[] { Core(), Kernel(), Todo("src/Kernel/Kernel.csproj") };
        Assert.Contains(MigrationBoundaryGate.Verify(Policy, MigrationBaseline.Empty, MigrationBaseline.Empty, snapshots),
            error => error.Contains("dependency", StringComparison.Ordinal));
    }

    [Fact]
    public void Adding_an_exception_to_baseline_is_rejected_even_if_the_graph_matches()
    {
        Assert.Contains(MigrationBoundaryGate.Verify(Policy, Debt(), MigrationBaseline.Empty,
            [Core(link: "modules/Todo.cs"), Kernel("plugins/Todo/Todo.csproj"), Todo()]),
            error => error.Contains("baseline grew", StringComparison.Ordinal));
    }

    [Fact]
    public void Unclassified_project_or_reference_is_rejected()
    {
        Assert.Contains(MigrationBoundaryGate.Verify(Policy, MigrationBaseline.Empty, MigrationBaseline.Empty,
            [Core(), Kernel("plugins/Hidden/Hidden.csproj"), Todo(), new("plugins/Hidden/Hidden.csproj", [], [])]),
            error => error.Contains("unclassified", StringComparison.Ordinal));
    }

    [Fact]
    public void Removed_project_classification_must_not_silently_hide_missing_project()
    {
        Assert.Contains(MigrationBoundaryGate.Verify(Policy, MigrationBaseline.Empty, MigrationBaseline.Empty, [Core(), Kernel()]),
            error => error.Contains("missing project", StringComparison.Ordinal));
    }

    [Fact]
    public void Bootstrap_owns_entrypoint_but_cannot_resume_compiling_module_implementation()
    {
        var policy = Policy with
        {
            Projects = Policy.Projects.Add(new("src/App/App.csproj", "bootstrap", ArchitectureLayer.Host, ArchitectureRole.Host))
        };
        var owned = new MigrationProject("src/App/App.csproj", ["src/App/App.xaml.cs", "src/App/Composition/Registration.cs"], []);
        Assert.Empty(MigrationBoundaryGate.Verify(policy, MigrationBaseline.Empty, MigrationBaseline.Empty,
            [Core(), Kernel(), Todo(), owned]));
        var linked = owned with { LinkedSources = owned.LinkedSources.Add("modules/Todo/Repository.cs") };
        Assert.Contains(MigrationBoundaryGate.Verify(policy, MigrationBaseline.Empty, MigrationBaseline.Empty,
            [Core(), Kernel(), Todo(), linked]), error => error.Contains("bootstrap compile", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("same", true)]
    [InlineData("removed", false)]
    [InlineData("redirected", false)]
    public void Directory_moves_preserve_exact_debt_without_restoring_removed_or_redirected_edges(string change, bool accepted)
    {
        const string oldHost = "host/DataManagement/src/FgoPet.DataManagement/FgoPet.DataManagement.csproj";
        const string oldDialogue = "modules/dialogue/src/FgoPet.Dialogue/FgoPet.Dialogue.csproj";
        const string host = "src/FgoPet.Desktop/DataManagement/FgoPet.DataManagement.csproj";
        const string dialogue = "plugins/FgoPet.Plugin.Dialogue/FgoPet.Dialogue.csproj";
        const string memory = "plugins/FgoPet.Plugin.Memory/Desktop/FgoPet.Memory.csproj";
        var policy = new ArchitecturePolicy(1,
            [new(host, "desktop", ArchitectureLayer.Host, ArchitectureRole.Host),
             new(dialogue, "dialogue", ArchitectureLayer.Implementation, ArchitectureRole.Production),
             new(memory, "memory", ArchitectureLayer.Desktop, ArchitectureRole.Production)], []);
        var target = change == "redirected" ? memory : dialogue;
        var current = new MigrationBaseline(1, [], [new(host, target, "compile")]);
        var original = change == "removed" ? MigrationBaseline.Empty
            : new MigrationBaseline(1, [], [new(oldHost, oldDialogue, "compile")]);
        var errors = MigrationBoundaryGate.Verify(policy, current, original,
            [new(host, [], [new(host, target, "compile")]), new(dialogue, [], []), new(memory, [], [])]);

        if (accepted) Assert.Empty(errors);
        else Assert.Contains(errors, error => error.Contains("baseline grew: dependency", StringComparison.Ordinal));
    }

    private static MigrationBaseline Debt() => new(1,
        [new("src/Core/Core.csproj", "modules/Todo.cs")],
        [new("src/Kernel/Kernel.csproj", "plugins/Todo/Todo.csproj", "compile")]);
    private static MigrationProject Core(string? link = null) => new("src/Core/Core.csproj", link is null ? [] : [link], []);
    private static MigrationProject Kernel(string? reference = null) => new("src/Kernel/Kernel.csproj", [], reference is null ? [] : [new("src/Kernel/Kernel.csproj", reference, "compile")]);
    private static MigrationProject Todo(string? reference = null) => new("plugins/Todo/Todo.csproj", [], reference is null ? [] : [new("plugins/Todo/Todo.csproj", reference, "compile")]);
}
