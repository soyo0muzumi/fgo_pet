using System.Text.Json;
using FgoPet.Core.Dialogue;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Kernel.Agent;
using FgoPet.Understanding;
using FgoPet.Understanding.Contracts;
using Xunit;

namespace FgoPet.Plugin.Understanding.Tests;

// Human-authored fixtures and an explicit host routing substitute prove local contracts, not semantic classification.
public sealed class UnderstandingH1AcceptanceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "fgopet-h1-owner-" + Guid.NewGuid().ToString("N"));
    private readonly ConversationScope _scope = new("mash", "fixture-project");
    private readonly RuntimeDatabase _database;
    private readonly UnderstandingService _owner;
    private readonly Sources _sources = new();
    public UnderstandingH1AcceptanceTests()
    {
        _database = new(Path.Combine(_directory, "runtime.db"), pooling: false);
        new RuntimeDatabaseMigrator(_database).Migrate();
        _owner = new(_database, _sources, routing: new FixtureRouting());
        _owner.Configure(_scope, 0, new(UnderstandingMode.Bounded, ReadSources: true, SaveCandidates: true, UseInDialogue: true));
    }
    private ConversationSourceSnapshot Root(string id, long revision = 1, ConversationSourceKind kind = ConversationSourceKind.User) =>
        new(new("fixture-conversation", "mash", "fixture-project"), id, new('A', 64),
            new(2026, 10, 18, 0, 0, 0, TimeSpan.Zero), kind, "Synthetic fixture", revision);
    private UnderstandingReceipt Apply(UnderstandingProposal proposal, string operation) => _owner.Apply(
        _owner.Begin(_scope, operation, proposal.Sources, coverage: proposal.Sources.Select(s => new UnderstandingSourceCoverage(s, 0, 4, 4)).ToArray()), [proposal]);
    private UnderstandingProposal Proposal(string id, UnderstandingDomain domain, UnderstandingKind kind, params ConversationSourceSnapshot[] roots) =>
        new(id, domain, kind, domain == UnderstandingDomain.World ? "world" : domain == UnderstandingDomain.Self ? "assistant" : "user",
            "synthetic-" + id, "only this fixture project; tentative; source statement is not verified truth", roots);

    [Theory]
    [InlineData(UnderstandingDomain.Self, UnderstandingKind.SelfReflection)]
    [InlineData(UnderstandingDomain.World, UnderstandingKind.ExternalObservation)]
    [InlineData(UnderstandingDomain.World, UnderstandingKind.Interpretation)]
    public void Host_classified_reflection_and_non_sensitive_world_material_can_take_bounded_path(UnderstandingDomain domain, UnderstandingKind kind)
    {
        var proposal = Proposal("allowed-fixture", domain, kind, Root("observed-behavior"), Root("completed-response", kind: ConversationSourceKind.Assistant));
        Assert.Equal(UnderstandingOutcome.Applied, Assert.Single(Apply(proposal, "positive").Items).Outcome);
        var entry = Assert.Single(_owner.Query(_scope, "").Items);
        Assert.Equal(UnderstandingReview.Unreviewed, entry.Review);
        Assert.Equal(UnderstandingEffect.Bounded, entry.Effect);
        Assert.Equal(proposal.Conditions, entry.Conditions);
        Assert.Equal(proposal.Sources, entry.Sources);
        Assert.Equal("model_inferred", entry.BasisKind);
        Assert.Equal(domain, entry.Domain); Assert.Equal(kind, entry.Kind);
    }

    [Theory]
    [InlineData(UnderstandingDomain.Self, UnderstandingKind.SelfReflection)]
    [InlineData(UnderstandingDomain.World, UnderstandingKind.ExternalObservation)]
    public void Same_fixture_without_host_routing_stays_pending(UnderstandingDomain domain, UnderstandingKind kind)
    {
        var owner = new UnderstandingService(_database, _sources);
        var proposal = Proposal("unclassified", domain, kind, Root("original"));
        var receipt = owner.Apply(owner.Begin(_scope, "unclassified-op", proposal.Sources,
            coverage: [new(proposal.Sources[0], 0, 4, 4)]), [proposal]);
        Assert.Equal(UnderstandingOutcome.ProposedPending, Assert.Single(receipt.Items).Outcome);
        Assert.Empty(owner.Query(_scope, "").Items);
        Assert.Single(owner.List(_scope, true));
    }

    [Fact] public void New_world_configuration_material_replaces_old_version_without_promoting_evidence_strength()
    {
        var old = Proposal("config-v1", UnderstandingDomain.World, UnderstandingKind.ExternalObservation, Root("config-original", 10));
        Assert.Equal(UnderstandingOutcome.Applied, Assert.Single(Apply(old, "old-config").Items).Outcome);
        var next = Proposal("config-v2", UnderstandingDomain.World, UnderstandingKind.ExternalObservation, Root("config-update", 11)) with
        { TargetId = old.Id, TargetRevision = 1 };
        Assert.Equal(UnderstandingOutcome.Applied, Assert.Single(Apply(next, "new-config").Items).Outcome);
        Assert.Equal(UnderstandingLife.Retracted, _owner.List(_scope, false).Single(e => e.Id == old.Id).Life);
        var projected = Assert.Single(_owner.Query(_scope, "").Items);
        Assert.Equal(next.Id, projected.Id); Assert.Equal(next.Conditions, projected.Conditions);
        Assert.Equal("model_inferred", projected.BasisKind);
        Assert.Equal(next.Sources, projected.Sources);
        Assert.Equal("UNDERSTANDING_REJECTED", Assert.Single(_owner.Review(_scope, "old-restore", old.Id, 2, UnderstandingAction.Restore).Items).Code);
    }

    [Fact] public async Task Real_owner_projection_keeps_relationship_pending_across_reopen_and_ordinary_native_query_paths()
    {
        var allowed = Proposal("visible-marker", UnderstandingDomain.Self, UnderstandingKind.CurrentInterest, Root("allowed-root"));
        var pending = Proposal("pending-relationship-marker", UnderstandingDomain.Relationship, UnderstandingKind.Interpretation, Root("pending-root"));
        Apply(allowed, "allowed-op"); Apply(pending, "pending-op");
        var reopened = new UnderstandingService(_database, _sources);
        await using var plugin = new UnderstandingPlugin(reopened, _ => true, new Resolver());
        await plugin.StartAsync(default);
        var scope = new ToolScope("fixture-conversation", "mash", "fixture-project");
        var ordinary = Assert.Single(plugin.BuildPrompt(scope, "", true));
        var native = Assert.Single(await plugin.BuildAsync(new(scope, "fixture-run", 1, new("query", new('A', 64)), 4096, []), default));
        Assert.Equal(ordinary.Text, native.Content); Assert.True(ordinary.Whole);
        Assert.Contains(allowed.Content, ordinary.Text); Assert.Contains(allowed.Conditions, ordinary.Text);
        Assert.DoesNotContain(pending.Content, ordinary.Text);
        var result = await Assert.Single(plugin.Contributions.Tools).InvokeAsync(new(scope, JsonSerializer.SerializeToElement(new { query = "" })), default);
        Assert.True(result.Success); Assert.DoesNotContain(pending.Content, result.Payload.GetRawText());
        Assert.Contains(allowed.Content, result.Payload.GetRawText());
        Assert.Single(reopened.List(_scope, true));
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    private sealed class FixtureRouting : IUnderstandingRoutingPolicy
    {
        public bool IsLowImpact(UnderstandingProposal proposal, IReadOnlyList<ConversationSourceSnapshot> roots) => proposal.Id != "pending-relationship-marker";
    }
    private sealed class Sources : IConversationSourceReader
    {
        public ConversationSourceSnapshot Read(ToolScope scope, string messageId, ConversationSourceKind kind) => throw new NotSupportedException();
        public bool IsCurrent(ConversationSourceSnapshot source) => true;
    }
    private sealed class Resolver : IAgentQueryResolver
    {
        public ValueTask<string?> ResolveAsync(ToolScope scope, ProtectedQueryReference reference, CancellationToken token) => ValueTask.FromResult<string?>("");
    }
}
