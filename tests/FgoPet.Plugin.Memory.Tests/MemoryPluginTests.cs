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
    private sealed class Sink : IMemoryCandidateSink
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
    }
}
