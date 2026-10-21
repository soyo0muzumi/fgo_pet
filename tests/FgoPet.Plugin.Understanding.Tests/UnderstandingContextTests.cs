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
