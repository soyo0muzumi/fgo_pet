using System.Collections.Immutable;
using System.Text.Json;
using FgoPet.Core.Dialogue;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Kernel.Agent;
using FgoPet.Understanding;
using FgoPet.Understanding.Contracts;
using Xunit;

namespace FgoPet.Plugin.Understanding.Tests;

public sealed class UnderstandingContextTests
{
    [Fact] public async Task Ordinary_native_and_query_share_projection_and_reject_pending_and_revoked_scope()
    {
        var reader = new Reader(); var allowed = true;
        await using var plugin = new UnderstandingPlugin(reader, _ => allowed, new Resolver());
        await plugin.StartAsync(default);
        var scope = new ToolScope("c", "mash", "p");
        var ordinary = Assert.Single(plugin.BuildPrompt(scope, "question", true));
        var request = new AgentContextRequest(scope, "run", 1, new("u", new('A', 64)), 1000, []);
        var native = Assert.Single(await plugin.BuildAsync(request, default));
        Assert.Equal(ordinary.Text, native.Content);
        Assert.True(ordinary.Whole);
        var tool = Assert.Single(plugin.Contributions.Tools);
        var result = await tool.InvokeAsync(new(scope, JsonSerializer.SerializeToElement(new { query = "" })), default);
        Assert.True(result.Success);
        Assert.Contains("暂定", result.Payload.GetRawText());
        allowed = false;
        Assert.Empty(plugin.BuildPrompt(scope, "question", true));
        Assert.Empty(await plugin.BuildAsync(request, default));
        Assert.Equal("UNDERSTANDING_SCOPE_DENIED", (await tool.InvokeAsync(new(scope, JsonSerializer.SerializeToElement(new { query = "" })), default)).ErrorCode);
    }

    [Fact] public async Task Native_budget_omits_whole_understanding_entry_instead_of_removing_conditions()
    {
        await using var plugin = new UnderstandingPlugin(new Reader(), _ => true, new Resolver());
        var catalog = PluginCatalog.Create([plugin]); await using var runtime = new PluginRuntime(catalog);
        Assert.True((await runtime.StartAsync(default)).Succeeded);
        var assembler = new AgentContextAssembler(catalog, runtime);
        var request = new AgentContextRequest(new("c", "mash", "p"), "run", 1, new("u", new('A', 64)), 80, []);
        ImmutableArray<ModelMessage> original = [new(ModelMessageRole.User, "question", [])];
        var result = await assembler.AssembleAsync(request, original, messages => messages.Sum(m => m.Content?.Length ?? 0), default);
        Assert.Single(result);
        Assert.Equal("question", result[0].Content);
    }
    [Fact] public async Task Disabled_understanding_does_not_resolve_or_read_the_current_user_query()
    {
        var resolver = new CountingResolver();
        await using var plugin = new UnderstandingPlugin(new DeniedReader(), _ => true, resolver); await plugin.StartAsync(default);
        var request = new AgentContextRequest(new("c", "mash", "p"), "run", 1, new("u", new('A', 64)), 1000, []);
        Assert.Empty(await plugin.BuildAsync(request, default)); Assert.Equal(0, resolver.Reads);
    }

    [Theory]
    [InlineData(UnderstandingOrigin.Expressed, UnderstandingStatus.Hypothesis)]
    [InlineData(UnderstandingOrigin.Enacted, UnderstandingStatus.Hypothesis)]
    [InlineData(UnderstandingOrigin.SecondOrderInferred, UnderstandingStatus.Hypothesis)]
    [InlineData(UnderstandingOrigin.ExternallySuggested, UnderstandingStatus.Hypothesis)]
    [InlineData(UnderstandingOrigin.ExternallySuggested, UnderstandingStatus.Tentative)]
    public async Task Plugin_defends_every_projection_against_approved_hypotheses_and_external_self(
        UnderstandingOrigin origin, UnderstandingStatus status)
    {
        var entry = Entry("blocked", UnderstandingDomain.Self) with { Origin = origin, Status = status };
        await using var plugin = new UnderstandingPlugin(new FixedReader([entry]), _ => true, new Resolver());
        await plugin.StartAsync(default);
        var scope = new ToolScope("c", "mash", "p");
        Assert.Empty(plugin.BuildPrompt(scope, "question", true));
        Assert.Empty(await plugin.BuildAsync(Request(scope), default));
        var tool = Assert.Single(plugin.Contributions.Tools);
        var result = await tool.InvokeAsync(new(scope, JsonSerializer.SerializeToElement(new { query = "" })), default);
        Assert.True(result.Success);
        Assert.Equal(0, result.Payload.GetProperty("Items").GetArrayLength());
    }

    [Theory]
    [InlineData(UnderstandingDomain.Self, 3)]
    [InlineData(UnderstandingDomain.Relationship, 2)]
    [InlineData(UnderstandingDomain.User, 3)]
    public async Task Domain_caps_apply_equally_to_ordinary_native_and_query_even_with_unbounded_reader(
        UnderstandingDomain domain, int limit)
    {
        // Exercise each cap independently, leaving the shared character budget ample room.
        var entries = Enumerable.Range(0, 7).Select(i => Entry("e" + i, domain)).ToArray();
        await using var plugin = new UnderstandingPlugin(new FixedReader(entries), _ => true, new Resolver());
        await plugin.StartAsync(default);
        var scope = new ToolScope("c", "mash", "p");
        var ordinary = plugin.BuildPrompt(scope, "question", true);
        Assert.Equal(limit, ordinary.Count);
        var native = await plugin.BuildAsync(Request(scope), default);
        Assert.Equal(ordinary.Select(b => b.Text), native.Select(b => b.Content));
        Assert.All(ordinary, b =>
        {
            using var json = JsonDocument.Parse(b.Text);
            Assert.Equal("Current understanding", json.RootElement.GetProperty("Label").GetString());
            Assert.Equal(domain.ToString(), json.RootElement.GetProperty("Entry").GetProperty("Domain").GetString());
        });
        var result = await Assert.Single(plugin.Contributions.Tools).InvokeAsync(
            new(scope, JsonSerializer.SerializeToElement(new { query = "" })), default);
        Assert.True(result.Success);
        Assert.Equal(limit, result.Payload.GetProperty("Items").GetArrayLength());
        Assert.True(result.Payload.GetProperty("Truncated").GetBoolean());
        Assert.All(result.Payload.GetProperty("Items").EnumerateArray(), item =>
            Assert.Equal("Current understanding", item.GetProperty("Label").GetString()));
    }

    [Fact] public async Task Maintenance_intents_remain_unchanged_data_with_only_readonly_query_contribution()
    {
        var entry = Entry("intent", UnderstandingDomain.Self) with { MaintenanceIntents = [
            UnderstandingMaintenanceIntent.PossibleStaleMemory, UnderstandingMaintenanceIntent.PossibleConflict,
            UnderstandingMaintenanceIntent.PossibleSpecialization, UnderstandingMaintenanceIntent.PossibleSupersession] };
        var before = JsonSerializer.Serialize(entry);
        await using var plugin = new UnderstandingPlugin(new FixedReader([entry]), _ => true, new Resolver());
        await plugin.StartAsync(default);
        var scope = new ToolScope("c", "mash", "p");
        var descriptor = Assert.Single(plugin.Contributions.Tools).Descriptor;
        Assert.Equal("understanding.query", descriptor.Name); Assert.Equal(ToolEffect.ReadOnly, descriptor.Effect);
        Assert.Empty(plugin.Contributions.TextInterpreters); Assert.Empty(plugin.Contributions.PostTurnObservers);
        Assert.Single(plugin.BuildPrompt(scope, "question", true));
        Assert.Single(await plugin.BuildAsync(Request(scope), default));
        var result = await Assert.Single(plugin.Contributions.Tools).InvokeAsync(
            new(scope, JsonSerializer.SerializeToElement(new { query = "" })), default);
        Assert.True(result.Success);
        var intents = result.Payload.GetProperty("Items")[0].GetProperty("Entry").GetProperty("MaintenanceIntents");
        Assert.Equal(entry.MaintenanceIntents!.Select(i => i.ToString()), intents.EnumerateArray().Select(i => i.GetString()));
        Assert.Equal(before, JsonSerializer.Serialize(entry));
    }

    private static AgentContextRequest Request(ToolScope scope) => new(scope, "run", 1, new("u", new('A', 64)), 10000, []);
    private static UnderstandingEntry Entry(string id, UnderstandingDomain domain) => new(id, new("mash", "p"), 1,
        domain, domain == UnderstandingDomain.Relationship ? UnderstandingKind.InteractionPattern : UnderstandingKind.CurrentStance,
        "assistant", "c", "p", "expressed", [], UnderstandingReview.Approved, UnderstandingEffect.Approved,
        UnderstandingLife.Active, DateTimeOffset.UnixEpoch, DateTimeOffset.MaxValue, 1,
        Origin: domain == UnderstandingDomain.User ? null : UnderstandingOrigin.Expressed,
        Status: domain == UnderstandingDomain.User ? null : UnderstandingStatus.Tentative);

    private sealed class FixedReader(IReadOnlyList<UnderstandingEntry> entries) : IUnderstandingReader
    {
        public UnderstandingProjection Query(ConversationScope scope, string query, bool historical = false, string? entryId = null) => new(1, entries);
    }

    private sealed class Reader : IUnderstandingReader
    {
        public UnderstandingProjection Query(ConversationScope scope, string query, bool historical = false, string? entryId = null) =>
            new(2, [new("entry", scope, 1, UnderstandingDomain.Self, UnderstandingKind.CurrentInterest, "assistant", "暂定的研究兴趣",
                "仅此项目；不作为用户事实；不得移除这些条件", "assistant_stance", [], UnderstandingReview.Unreviewed,
                UnderstandingEffect.Bounded, UnderstandingLife.Active, DateTimeOffset.UnixEpoch, DateTimeOffset.MaxValue, 1)]);
    }
    private sealed class Resolver : IAgentQueryResolver
    { public ValueTask<string?> ResolveAsync(ToolScope scope, ProtectedQueryReference reference, CancellationToken token) => ValueTask.FromResult<string?>("question"); }
    private sealed class DeniedReader : IUnderstandingReader
    { public UnderstandingProjection Query(ConversationScope scope, string query, bool historical = false, string? entryId = null) => new(0, [], "UNDERSTANDING_DISABLED"); }
    private sealed class CountingResolver : IAgentQueryResolver
    {
        public int Reads;
        public ValueTask<string?> ResolveAsync(ToolScope scope, ProtectedQueryReference reference, CancellationToken token) { Reads++; return ValueTask.FromResult<string?>("question"); }
    }
}
