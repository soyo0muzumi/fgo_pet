using System.Collections.Immutable;
using System.Text.Json;
using FgoPet.App.Dialogue;
using FgoPet.App.Memory;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Memory;
using FgoPet.Extensibility;
using FgoPet.Plugin.Memory;
using Xunit;

namespace FgoPet.Plugin.Memory.Tests;

public sealed class MemoryPluginTests
{
    private static readonly ToolScope Scope = new("conversation", "role", "project");
    private static ConversationCompletedTurn Turn => new(Scope, "user", "user statement", "assistant",
        "reply", "suggested fact", null, default);

    [Fact]
    public async Task Disabled_and_stopping_capabilities_neither_recall_nor_stage()
    {
        var recall = new Recall();
        var sink = new Sink();
        var enabled = true;
        using var stopping = new CancellationTokenSource();
        await using var plugin = new MemoryPlugin(recall, new Sources(), sink, enabled: () => enabled);
        Assert.Empty(plugin.BuildPrompt(Scope, "query", false));
        await plugin.StartAsync(stopping.Token);
        Assert.Single(plugin.BuildPrompt(Scope, "query", false));
        enabled = false;
        Assert.Empty(plugin.BuildPrompt(Scope, "query", false));
        plugin.Observe(Turn);
        enabled = true;
        stopping.Cancel();
        Assert.Empty(plugin.BuildPrompt(Scope, "query", false));
        plugin.Observe(Turn);
        Assert.Equal(1, recall.Reads);
        Assert.Equal(0, sink.Begun);
    }

    [Fact]
    public async Task Explicit_memory_request_gets_review_guidance_only_when_candidate_extraction_is_enabled()
    {
        const string explicitRequest = "帮我记住：我偏好铜枝方格本。";
        var resolver = new QueryResolver(explicitRequest);
        var sink = new Sink();
        var enabled = true;
        var queue = new MemoryExtractionQueue(new EmptyExtractor(), sink, sink, new DialogueContextLifetime(), _ => true);
        await using var plugin = new MemoryPlugin(new Recall(), new Sources(), sink, queue, () => enabled, resolver);
        await plugin.StartAsync(default);

        var request = new AgentContextRequest(Scope, "run", 2, new("user", "fingerprint"), 4000,
            ImmutableArray<ToolDescriptor>.Empty);
        var guidance = Assert.Single(plugin.Contributions.AgentContexts.Where(context => context.ProvidesTrustedInstructions));
        var instruction = Assert.Single(await guidance.BuildAsync(request, default));
        Assert.Equal(AgentContextKind.Instruction, instruction.Kind);
        Assert.Contains("候选", instruction.Content);
        Assert.Contains("确认", instruction.Content);
        Assert.DoesNotContain("铜枝方格本", instruction.Content);
        Assert.Contains(plugin.BuildPrompt(Scope, explicitRequest, false), block => block.Kind == ConversationPromptBlockKind.Instruction);

        foreach (var casual in new[] { "我还记得铜枝方格本。", "请不要记住这件事。", "她说‘帮我记住’是什么意思？" })
        {
            resolver.Query = casual;
            Assert.Empty(await guidance.BuildAsync(request, default));
            Assert.DoesNotContain(plugin.BuildPrompt(Scope, casual, false), block => block.Kind == ConversationPromptBlockKind.Instruction);
        }
        resolver.Query = explicitRequest;
        enabled = false;
        Assert.Empty(await guidance.BuildAsync(request, default));
        Assert.Empty(plugin.BuildPrompt(Scope, explicitRequest, false));
    }

    [Fact]
    public async Task A_staging_failure_still_abandons_the_exact_source_ticket()
    {
        var sink = new Sink { FailStage = true };
        await using var plugin = new MemoryPlugin(new Recall(), new Sources(), sink);
        await plugin.StartAsync(default);
        Assert.Throws<InvalidOperationException>(() => plugin.Observe(Turn));
        Assert.Same(sink.Ticket, sink.Abandoned);
        Assert.Equal(MemoryEvidenceKind.AssistantSuggestion, sink.Ticket!.Source.Kind);
        Assert.Equal("assistant", sink.Ticket.Source.MessageId);
        Assert.Equal("project", sink.Ticket.Source.Scope.ProjectId);
        Assert.Equal("project label", sink.Ticket.Source.ProjectLabel);
    }

    [Fact]
    public async Task Prompt_reads_are_fresh_and_whole_and_never_cross_role_or_project()
    {
        var recall = new Recall();
        await using var plugin = new MemoryPlugin(recall, new Sources());
        await plugin.StartAsync(default);
        var first = Assert.Single(plugin.BuildPrompt(Scope, "query", false));
        recall.Text = "updated confirmed fact";
        var second = Assert.Single(plugin.BuildPrompt(Scope, "query", false));
        Assert.NotEqual(first.Text, second.Text);
        Assert.Equal(ConversationPromptBlockKind.Data, second.Kind);
        Assert.True(second.Whole);
        Assert.Contains("来源信息不完整", second.Text);
        await plugin.StopAsync(default);
        Assert.Empty(plugin.BuildPrompt(Scope, "query", false));
    }

    [Fact]
    public async Task Unmatched_turns_use_no_confirmed_memory_on_any_reply_path()
    {
        var recall = new MemoryRecallService(new BackgroundReader());
        const string query = "几何绘图";
        Assert.Single(recall.Query(new(Scope.RoleId, Scope.ProjectId), query).Items);
        await using var plugin = new MemoryPlugin(recall, new Sources(), queryResolver: new QueryResolver(query));
        await plugin.StartAsync(default);

        Assert.Empty(plugin.BuildPrompt(Scope, query, false));
        var contributions = plugin.Contributions;
        var request = new AgentContextRequest(Scope, "run", 1, new("user", "fingerprint"), 4000,
            ImmutableArray<ToolDescriptor>.Empty);
        Assert.Empty(await Assert.Single(contributions.AgentContexts).BuildAsync(request, default));
        var search = await Assert.Single(contributions.Tools).InvokeAsync(
            new ToolInvocation(Scope, JsonSerializer.SerializeToElement(new { query })), default);
        Assert.True(search.Success);
        Assert.Empty(search.Payload.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Contributions_add_bounded_tools_and_only_add_context_with_host_query_resolver()
    {
        var withoutResolverRecall = new Recall();
        await using (var withoutResolver = new MemoryPlugin(withoutResolverRecall, new Sources()))
        {
            await withoutResolver.StartAsync(default);
            var legacyContributions = withoutResolver.Contributions;

            Assert.Equal("memory.search", Assert.Single(legacyContributions.Tools).Descriptor.Name);
            Assert.Empty(legacyContributions.AgentContexts);
            Assert.Same(withoutResolver, Assert.Single(legacyContributions.Prompts));
            Assert.Same(withoutResolver, Assert.Single(legacyContributions.PostTurnObservers));
            Assert.Empty(legacyContributions.AgentObservers);
        }

        var recall = new Recall();
        var resolver = new QueryResolver("private original query");
        var enabled = true;
        await using var plugin = new MemoryPlugin(recall, new Sources(), enabled: () => enabled,
            queryResolver: resolver);
        await plugin.StartAsync(default);
        var contributions = plugin.Contributions;
        var search = Assert.Single(contributions.Tools);
        var context = Assert.Single(contributions.AgentContexts);
        Assert.Equal("memory.search", search.Descriptor.Name);
        Assert.Equal("confirmed-memory", context.Id);
        Assert.Same(plugin, Assert.Single(contributions.Prompts));
        Assert.Same(plugin, Assert.Single(contributions.PostTurnObservers));
        Assert.Empty(contributions.AgentObservers);

        var searchArguments = JsonSerializer.SerializeToElement(new { query = "fact" });
        var searchResult = await search.InvokeAsync(new ToolInvocation(Scope, searchArguments), default);
        Assert.True(searchResult.Success);
        Assert.Contains("confirmed fact", searchResult.Payload.GetRawText());

        var reference = new ProtectedQueryReference("user-message", "fingerprint");
        var request = new AgentContextRequest(Scope, "run", 1, reference, 4000,
            ImmutableArray<ToolDescriptor>.Empty);
        var contextBlocks = await context.BuildAsync(request, default);
        Assert.Equal("confirmed-memory", Assert.Single(contextBlocks).Source);
        Assert.DoesNotContain("private original query", contextBlocks[0].Content);
        Assert.Equal(1, resolver.Calls);

        var readCountBeforeDisable = recall.Reads;
        enabled = false;
        var unavailableSearch = await search.InvokeAsync(new ToolInvocation(Scope, searchArguments), default);
        Assert.False(unavailableSearch.Success);
        Assert.Equal("MEMORY_UNAVAILABLE", unavailableSearch.ErrorCode);
        Assert.Equal("{}", unavailableSearch.Payload.GetRawText());
        var unavailableContext = await context.BuildAsync(request, default);
        Assert.Equal("MEMORY_UNAVAILABLE", Assert.Single(unavailableContext).Content);
        Assert.Equal(readCountBeforeDisable, recall.Reads);
        Assert.Equal(1, resolver.Calls);
    }

    private sealed class BackgroundReader : IMemorySnapshotReader
    {
        public MemoryRecallSnapshot ReadSnapshot(MemoryScope scope) =>
            new(1, [new("general", Scope.RoleId, "避开花生", true, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)]);
    }

    private sealed class Recall : IMemoryRecall
    {
        public int Reads { get; private set; }
        public string Text = "confirmed fact";
        public MemoryRecallSnapshot Query(MemoryScope scope, string query, int maxItems = 8, int maxChars = 6000)
        {
            Reads++;
            var now = DateTimeOffset.UnixEpoch;
            return new(Reads, [new("current", "role", Text, true, now, now, projectId: "project"),
                new("other-role", "other", "private", true, now, now),
                new("other-project", "role", "private", true, now, now, projectId: "other")]);
        }
    }
    private sealed class Sources : IConversationSourceReader
    {
        public ConversationSourceSnapshot Read(ToolScope scope, string messageId, ConversationSourceKind kind) =>
            new(scope, messageId, "fingerprint", DateTimeOffset.UnixEpoch, kind, "project label");
        public bool IsCurrent(ConversationSourceSnapshot source) => true;
    }
    private sealed class QueryResolver(string? query) : IAgentQueryResolver
    {
        public string? Query { get; set; } = query;
        public int Calls { get; private set; }
        public ValueTask<string?> ResolveAsync(ToolScope scope, ProtectedQueryReference reference,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Calls++;
            return ValueTask.FromResult(Query);
        }
    }
    private sealed class EmptyExtractor : IMemoryCandidateExtractor
    {
        public Task<IReadOnlyList<MemoryProposal>> ExtractAsync(MemoryExtractionWork work, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MemoryProposal>>([]);
    }
    private sealed class Sink : IMemoryCandidateSink, IMemoryWriteLifetime
    {
        public bool FailStage;
        public int Begun;
        public MemoryWriteTicket? Ticket;
        public MemoryWriteTicket? Abandoned;
        public MemoryWriteTicket Begin(MemorySource source)
        {
            Begun++;
            return Ticket = new("ticket", "generation", 1, source);
        }
        public MemoryStageResult Stage(MemoryWriteTicket ticket, IReadOnlyList<MemoryProposal> proposals)
        {
            if (FailStage) throw new InvalidOperationException("test staging failure");
            return new(MemoryStageStatus.Staged, ["candidate"]);
        }
        public void Abandon(MemoryWriteTicket ticket) => Abandoned = ticket;
        public void StartSession() { }
        public void InvalidateWrites() { }
    }
}
