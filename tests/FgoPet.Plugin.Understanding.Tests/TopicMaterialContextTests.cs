using FgoPet.Core.Dialogue;
using FgoPet.Extensibility;
using FgoPet.Understanding;
using FgoPet.Understanding.Contracts;
using Xunit;

namespace FgoPet.Plugin.Understanding.Tests;

public sealed class TopicMaterialContextTests
{
    private static readonly ToolScope Scope = new("conversation", "mash", "project");

    [Fact]
    public async Task Ordinary_and_native_offer_one_identical_optional_provenance_bearing_material()
    {
        var clock = new Clock(); var store = new Store(); var reader = new Reader(clock);
        store.Items.Add(Material(clock));
        using var plugin = new TopicMaterialPlugin(store, store.MarkUsed, reader, _ => true, new Resolver(), clock);
        await plugin.StartAsync(default);
        var ordinary = Assert.Single(plugin.BuildPrompt(Scope, "question", false));
        var native = Assert.Single(await plugin.BuildAsync(Request(), default));
        Assert.Equal("topic-material", ordinary.Source);
        Assert.Equal("material", native.Source);
        Assert.Equal(ordinary.Text, native.Content);
        Assert.Equal(ConversationPromptBlockKind.Data, ordinary.Kind);
        Assert.Equal(AgentContextKind.Data, native.Kind);
        Assert.True(ordinary.Whole); Assert.True(native.Whole);
        Assert.False(plugin.ProvidesTrustedInstructions);
        Assert.Contains("Selective forgetting suppresses weak associations.", ordinary.Text);
        Assert.Contains("https://example.org/memory", ordinary.Text);
        Assert.Contains("source_kind: external_material", ordinary.Text);
        Assert.Contains("Expires at:", ordinary.Text);
        Assert.Contains("Connects to the current memory experiment.", ordinary.Text);
        Assert.Contains("optional", ordinary.Text);
        Assert.Contains("search snippet", ordinary.Text);
        Assert.Empty(store.Used);
    }

    [Fact]
    public async Task Denied_or_empty_admission_does_not_read_material_or_protected_query()
    {
        var clock = new Clock(); var store = new Store(); var reader = new Reader(clock);
        var resolver = new Resolver();
        using var plugin = new TopicMaterialPlugin(store, reader, _ => true, resolver, clock);
        await plugin.StartAsync(default);
        reader.Code = "UNDERSTANDING_DISABLED";
        Assert.Empty(plugin.BuildPrompt(Scope, "question", false));
        Assert.Empty(await plugin.BuildAsync(Request(), default));
        reader.Code = null; reader.Items.Clear();
        Assert.Empty(await plugin.BuildAsync(Request(), default));
        Assert.Equal(0, store.Reads); Assert.Equal(0, resolver.Reads);
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("dormant")]
    [InlineData("expired-interest")]
    [InlineData("rejected")]
    [InlineData("different-project")]
    [InlineData("different-role")]
    [InlineData("revised-interest")]
    [InlineData("not-interest")]
    public async Task Ineligible_or_revised_interest_never_supplies_material(string condition)
    {
        var clock = new Clock(); var store = new Store(); var reader = new Reader(clock);
        store.Items.Add(Material(clock));
        var entry = reader.Items[0];
        reader.Items[0] = condition switch
        {
            "pending" => entry with { Effect = UnderstandingEffect.Pending },
            "dormant" => entry with { Life = UnderstandingLife.Dormant },
            "expired-interest" => entry with { ActiveUntil = clock.Now },
            "rejected" => entry with { Review = UnderstandingReview.Rejected },
            "different-project" => entry with { Scope = new("mash", "other") },
            "different-role" => entry with { Scope = new("other", "project") },
            "revised-interest" => entry with { Revision = 2 },
            _ => entry with { Kind = UnderstandingKind.CurrentStance }
        };
        using var plugin = new TopicMaterialPlugin(store, reader, _ => true, new Resolver(), clock);
        await plugin.StartAsync(default);
        Assert.Empty(plugin.BuildPrompt(Scope, "question", false));
        Assert.Empty(await plugin.BuildAsync(Request(), default));
    }

    [Fact]
    public async Task Selection_prioritizes_material_not_recently_offered_but_allows_later_reuse()
    {
        var clock = new Clock(); var store = new Store(); var reader = new Reader(clock);
        store.Items.Add(Material(clock) with { Id = "recent", Summary = "Recently offered.", LastUsedAt = clock.Now });
        store.Items.Add(Material(clock) with { Id = "older", Summary = "An unused older material.", FetchedAt = clock.Now.AddHours(-1) });
        using var plugin = new TopicMaterialPlugin(store, reader, _ => true, new Resolver(), clock);
        await plugin.StartAsync(default);
        Assert.Contains("An unused older material.", Assert.Single(plugin.BuildPrompt(Scope, "question", false)).Text);
        store.Items.RemoveAt(1);
        Assert.Contains("Recently offered.", Assert.Single(plugin.BuildPrompt(Scope, "question", false)).Text);
        clock.Now = clock.Now.AddHours(1);
        Assert.Single(plugin.BuildPrompt(Scope, "question", false));
        store.Items[0] = store.Items[0] with { ExpiresAt = clock.Now };
        Assert.Empty(plugin.BuildPrompt(Scope, "question", false));
    }

    [Fact]
    public async Task Completion_records_only_an_offered_candidate_and_rechecks_revision_and_grants()
    {
        var clock = new Clock(); var store = new Store(); var reader = new Reader(clock);
        store.Items.Add(Material(clock));
        using var plugin = new TopicMaterialPlugin(store, reader, _ => true, new Resolver(), clock);
        await plugin.StartAsync(default);
        plugin.Observe(Turn()); Assert.Empty(store.Used);
        Assert.Single(plugin.BuildPrompt(Scope, "question", false));
        plugin.Observe(Turn()); Assert.Equal(new[] { "material" }, store.Used);
        plugin.Observe(Turn()); Assert.Single(store.Used);
        Assert.Single(plugin.BuildPrompt(Scope, "question", false));
        reader.Items[0] = reader.Items[0] with { Revision = 2 };
        plugin.Observe(Turn()); Assert.Single(store.Used);
        reader.Items[0] = reader.Items[0] with { Revision = 1 };
        Assert.Single(plugin.BuildPrompt(Scope, "question", false));
        reader.Code = "UNDERSTANDING_NOT_AUTHORIZED";
        plugin.Observe(Turn()); Assert.Single(store.Used);
    }

    [Fact]
    public async Task Scope_revocation_or_shutdown_removes_admission_and_pending_usage()
    {
        var clock = new Clock(); var store = new Store(); var reader = new Reader(clock);
        store.Items.Add(Material(clock)); var allowed = true;
        using var plugin = new TopicMaterialPlugin(store, reader, _ => allowed, new Resolver(), clock);
        await plugin.StartAsync(default);
        Assert.Single(plugin.BuildPrompt(Scope, "question", false));
        allowed = false; plugin.Observe(Turn()); Assert.Empty(store.Used);
        Assert.Empty(await plugin.BuildAsync(Request(), default));
        allowed = true; Assert.Single(plugin.BuildPrompt(Scope, "question", false));
        await plugin.StopAsync(default);
        Assert.Empty(plugin.BuildPrompt(Scope, "question", false));
        Assert.Empty(await plugin.BuildAsync(Request(), default));
        plugin.Observe(Turn()); Assert.Empty(store.Used);
    }

    [Fact]
    public async Task Native_rechecks_admission_after_awaiting_query_resolution()
    {
        var clock = new Clock(); var store = new Store(); var reader = new Reader(clock);
        store.Items.Add(Material(clock));
        var resolver = new Resolver { OnResolve = () => reader.Code = "UNDERSTANDING_DISABLED" };
        using var plugin = new TopicMaterialPlugin(store, reader, _ => true, resolver, clock);
        await plugin.StartAsync(default);
        Assert.Empty(await plugin.BuildAsync(Request(), default));
        Assert.Equal(0, store.Reads);
    }

    [Fact]
    public async Task Offers_are_bounded_across_conversations_and_do_not_mark_an_evicted_offer()
    {
        var clock = new Clock(); var store = new Store(); var reader = new Reader(clock);
        store.Items.Add(Material(clock));
        using var plugin = new TopicMaterialPlugin(store, reader, _ => true, new Resolver(), clock);
        await plugin.StartAsync(default);
        for (var index = 0; index < 65; index++)
        {
            var scope = new ToolScope("conversation-" + index, "mash", "project-" + index);
            reader.Items[0] = reader.Items[0] with { Scope = new("mash", scope.ProjectId) };
            Assert.Single(plugin.BuildPrompt(scope, "question", false));
        }
        reader.Items[0] = reader.Items[0] with { Scope = new("mash", "project-0") };
        plugin.Observe(Turn() with { Scope = new("conversation-0", "mash", "project-0") });
        Assert.Empty(store.Used);
        reader.Items[0] = reader.Items[0] with { Scope = new("mash", "project-64") };
        plugin.Observe(Turn() with { Scope = new("conversation-64", "mash", "project-64") });
        Assert.Single(store.Used);
    }

    [Fact]
    public async Task Open_question_material_is_available_only_for_the_corresponding_kind()
    {
        var clock = new Clock(); var store = new Store(); var reader = new Reader(clock);
        store.Items.Add(Material(clock) with { Scope = TopicMaterialScope.OpenQuestion });
        using var plugin = new TopicMaterialPlugin(store, reader, _ => true, new Resolver(), clock);
        await plugin.StartAsync(default);
        Assert.Empty(plugin.BuildPrompt(Scope, "question", false));
        reader.Items[0] = reader.Items[0] with { Kind = UnderstandingKind.OpenQuestion };
        Assert.Single(plugin.BuildPrompt(Scope, "question", false));
        plugin.Observe(Turn() with { UserText = "another question" });
        Assert.Empty(store.Used);
    }

    private static TopicMaterial Material(Clock clock) => new("material", "interest", 1, TopicMaterialScope.Interest,
        "Selective forgetting suppresses weak associations.", "Connects to the current memory experiment.",
        "Synthetic source", "https://example.org/memory", clock.Now, clock.Now.AddDays(1));
    private static AgentContextRequest Request() => new(Scope, "run", 1, new("user", new('A', 64)), 4000, []);
    private static ConversationCompletedTurn Turn() => new(Scope, "user", "question", "assistant", "answer", null, null, default);
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-10-31T00:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Reader : IUnderstandingReader
    {
        public string? Code;
        public List<UnderstandingEntry> Items;
        public Reader(Clock clock) => Items = [new("interest", new("mash", "project"), 1, UnderstandingDomain.Self,
            UnderstandingKind.CurrentInterest, "assistant", "Memory research", "Current project only", "assistant_stance", [],
            UnderstandingReview.Approved, UnderstandingEffect.Approved, UnderstandingLife.Active, clock.Now, clock.Now.AddDays(1), 1)];
        public UnderstandingProjection Query(ConversationScope scope, string query, bool historical = false, string? entryId = null) => new(1, Items.ToArray(), Code);
    }
    private sealed class Resolver : IAgentQueryResolver
    {
        public int Reads;
        public Action? OnResolve;
        public ValueTask<string?> ResolveAsync(ToolScope scope, ProtectedQueryReference reference, CancellationToken token)
        { Reads++; OnResolve?.Invoke(); return ValueTask.FromResult<string?>("question"); }
    }
    private sealed class Store : ITopicMaterialStore
    {
        public List<TopicMaterial> Items = [];
        public List<string> Used = [];
        public int Reads;
        public void Add(ConversationScope scope, TopicMaterial material) => Items.Add(material);
        public IReadOnlyList<TopicMaterial> GetActive(ConversationScope scope) { Reads++; return Items.ToArray(); }
        public void MarkUsed(ConversationScope scope, string materialId, DateTimeOffset usedAt) => Used.Add(materialId);
        public bool TryReserveResearch(ConversationScope scope, string topicKey, DateTimeOffset now, TimeSpan refreshAfter, int dailyLimit = 6) => true;
        public bool CanResearch(ConversationScope scope, string topicKey, DateTimeOffset now, TimeSpan refreshAfter, int dailyLimit = 6) => true;
        public void DeleteScope(ConversationScope scope) => Items.Clear();
    }
}
