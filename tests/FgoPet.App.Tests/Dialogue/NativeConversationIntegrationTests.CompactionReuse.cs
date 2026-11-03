using System.Text.Json;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Packs;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Kernel.Agent;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

public sealed partial class NativeConversationIntegrationTests
{
    [Fact]
    public async Task Full_envelope_compaction_is_reused_by_five_short_followups()
    {
        var provider = new ScriptedProvider(Enumerable.Range(0, 12).Select(_ => ProviderTurn.Final("done")).ToArray());
        var summaries = new ReuseSummaryRecorder();
        var diagnostics = new List<ContextBudgetDiagnostic>();
        await using var host = await TestHost.CreateAsync(provider, new RecordingTool(ToolEffect.ReadOnly),
            new RecordingObserver(), TestSettingsStore.Create(50000), enableSummary: true,
            summarizer: summaries, context: new ReuseContext(10000), contextDiagnostics: diagnostics.Add);
        SeedReusableHistory(host);
        var original = host.Conversations.LoadMessages("reuse-conversation", RoleId).ToArray();
        var stages = new List<ConversationRequestStage>();
        host.Orchestrator.Updated += update =>
        {
            if (update.RequestStage is { } stage) stages.Add(stage);
        };
        summaries.Starting = () => Assert.Equal(ConversationRequestStage.Compacting, stages[^1]);

        var first = await host.Orchestrator.SendAsync(RoleId, "continue", default, new(ProjectId));
        Assert.Equal(ConversationSendStatus.Completed, first.Status);
        Assert.Contains(diagnostics, d => d.Stage == ContextDiagnosticStage.BeforeCompaction && d.Step == 0
            && d.MeasuredInput < d.TriggerTokens);
        Assert.Contains(diagnostics, d => d.Stage == ContextDiagnosticStage.BeforeCompaction && d.Step == 1
            && d.MeasuredInput >= d.TriggerTokens);
        Assert.Single(summaries.Requests);
        // Reopen the real SQLite projection: an in-memory working summary cannot satisfy this.
        var store = new SqliteConversationContextStore(new RuntimeDatabase(host.Database.DatabasePath, pooling: false));
        var saved = store.Read(new(RoleId, ProjectId), first.ConversationId);
        Assert.NotNull(saved.Summary);
        Assert.InRange(saved.Summary.CoveredThroughSequence, 2, 4);
        Assert.Equal(1, stages.Count(stage => stage == ConversationRequestStage.Compacting));
        Assert.Contains(diagnostics, d => d.Stage == ContextDiagnosticStage.CompactionCommitted);
        Assert.DoesNotContain(diagnostics, d => d.Stage == ContextDiagnosticStage.CompactionApplied);
        Assert.All(provider.Requests, request => Assert.Contains(request.ModelMessages,
            m => m.Content.Contains("conversation_summary", StringComparison.Ordinal)));

        for (var turn = 1; turn <= 5; turn++)
        {
            stages.Clear();
            var start = provider.Requests.Count;
            var result = await host.Orchestrator.SendAsync(RoleId, "ok", default, new(ProjectId));
            Assert.Equal(ConversationSendStatus.Completed, result.Status);
            Assert.Single(summaries.Requests);
            Assert.DoesNotContain(ConversationRequestStage.Compacting, stages);
            Assert.Equal(saved.ProjectionRevision, store.Read(new(RoleId, ProjectId), result.ConversationId).ProjectionRevision);
            Assert.All(provider.Requests.Skip(start), request =>
            {
                Assert.Contains(request.ModelMessages, m => m.Content.Contains("conversation_summary", StringComparison.Ordinal));
                Assert.DoesNotContain(request.ModelMessages, m => m.Content.Contains("old-source-1", StringComparison.Ordinal));
                Assert.Single(request.ModelMessages.Where(m => m.Content.Contains("fresh-reuse-context", StringComparison.Ordinal)));
            });
            Console.WriteLine(JsonSerializer.Serialize(new { timestampUtc = DateTimeOffset.UtcNow,
                phase = "compaction_followup", turn, result = "completed", summaryCalls = summaries.Requests.Count,
                projectionRevision = saved.ProjectionRevision }));
        }
        Assert.Equal(original, host.Conversations.LoadMessages(first.ConversationId, RoleId).Take(original.Length));
        // Reuse must not become a permanent cooldown: sufficiently large new history still compacts.
        for (var turn = 0; turn < 6; turn++)
            Assert.Equal(ConversationSendStatus.Completed,
                (await host.Orchestrator.SendAsync(RoleId, new string('g', 5000), default, new(ProjectId))).Status);
        Assert.True(summaries.Requests.Count > 1);
        Assert.True(store.Read(new(RoleId, ProjectId), first.ConversationId).ProjectionRevision > saved.ProjectionRevision);
        Assert.All(summaries.Requests, request => Assert.DoesNotContain(request.Messages,
            message => message.Text.Contains("fresh-reuse-context", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Historical_tool_results_are_compacted_once_then_reused_without_reexecution()
    {
        var script = Enumerable.Range(0, 4).SelectMany(index => new[] {
            ProviderTurn.Call("fixture.read", "history-tool-" + index, "{}"), ProviderTurn.Final("done")
        }).Concat(Enumerable.Range(0, 6).Select(_ => ProviderTurn.Final("done"))).ToArray();
        var provider = new ScriptedProvider(script);
        var tool = new RecordingTool(ToolEffect.ReadOnly, "history-tool-value:" + new string('t', 6000));
        var summaries = new ReuseSummaryRecorder();
        await using var host = await TestHost.CreateAsync(provider, tool, new RecordingObserver(),
            TestSettingsStore.Create(50000), enableSummary: true, summarizer: summaries, context: new ReuseContext(10000));
        string conversationId = "";
        for (var turn = 0; turn < 4; turn++)
        {
            var result = await host.Orchestrator.SendAsync(RoleId, "read", default, new(ProjectId));
            Assert.Equal(ConversationSendStatus.Completed, result.Status);
            conversationId = result.ConversationId;
        }
        var original = host.Conversations.LoadMessages(conversationId, RoleId).ToArray();
        var first = await host.Orchestrator.SendAsync(RoleId, "continue", default, new(ProjectId));
        Assert.Equal(ConversationSendStatus.Completed, first.Status);
        var saved = host.ContextStore!.Read(new(RoleId, ProjectId), conversationId);
        Assert.NotNull(saved.Summary);
        var calls = summaries.Requests.Count;
        Assert.Contains(summaries.Requests.SelectMany(request => request.Messages), message =>
            message.Text.Contains("tool_records:", StringComparison.Ordinal)
            && message.Text.Contains("history-tool-value", StringComparison.Ordinal));
        var stages = new List<ConversationRequestStage>();
        host.Orchestrator.Updated += update =>
        {
            if (update.RequestStage is { } stage) stages.Add(stage);
        };
        for (var turn = 1; turn <= 5; turn++)
        {
            Assert.Equal(ConversationSendStatus.Completed,
                (await host.Orchestrator.SendAsync(RoleId, "ok", default, new(ProjectId))).Status);
            Assert.Equal(calls, summaries.Requests.Count);
            Assert.Equal(saved.ProjectionRevision, host.ContextStore.Read(new(RoleId, ProjectId), conversationId).ProjectionRevision);
            Console.WriteLine(JsonSerializer.Serialize(new { timestampUtc = DateTimeOffset.UtcNow,
                phase = "tool_history_followup", turn, result = "completed", summaryCalls = calls,
                projectionRevision = saved.ProjectionRevision }));
        }
        Assert.DoesNotContain(ConversationRequestStage.Compacting, stages);
        Assert.Equal(4, tool.Invocations.Count);
        Assert.Equal(original, host.Conversations.LoadMessages(conversationId, RoleId).Take(original.Length));
        var runs = await host.Runs.LoadConversationRunsAsync(new(conversationId, RoleId, ProjectId),
            original.Where(message => message.Role == ChatMessageRole.User).Select(message => message.MessageId).ToArray(), default);
        Assert.Equal(4, runs.Count);
        Assert.All(runs, run => Assert.Contains(run.CompletedSteps.SelectMany(step => step.Calls), call =>
            call.Call.Name == "fixture.read" && call.Result!.Payload.GetProperty("value").GetString()!.StartsWith("history-tool-value:", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_or_cancelled_full_envelope_summary_preserves_the_original_projection(bool cancel)
    {
        var provider = new ScriptedProvider(ProviderTurn.Final("done"));
        var summaries = new ReuseSummaryRecorder { Invalid = !cancel };
        await using var host = await TestHost.CreateAsync(provider, new RecordingTool(ToolEffect.ReadOnly),
            new RecordingObserver(), TestSettingsStore.Create(50000), enableSummary: true,
            summarizer: summaries, context: new ReuseContext(10000));
        SeedReusableHistory(host);
        var original = host.Conversations.LoadMessages("reuse-conversation", RoleId).ToArray();
        if (cancel) summaries.Starting = host.Orchestrator.CancelCurrent;
        var result = await host.Orchestrator.SendAsync(RoleId, "continue", default, new(ProjectId));
        Assert.NotEmpty(summaries.Requests);
        var saved = host.ContextStore!.Read(new(RoleId, ProjectId), "reuse-conversation");
        Assert.Null(saved.Summary);
        Assert.Equal(0, saved.ProjectionRevision);
        Assert.Equal(original, host.Conversations.LoadMessages("reuse-conversation", RoleId).Take(original.Length));
        if (cancel)
        {
            Assert.Equal(ConversationSendStatus.Cancelled, result.Status);
            Assert.Empty(provider.Requests);
        }
    }

    [Fact]
    public async Task Threshold_without_eligible_history_does_not_show_compacting()
    {
        var provider = new ScriptedProvider(ProviderTurn.Final("done"));
        var summaries = new ReuseSummaryRecorder();
        var diagnostics = new List<ContextBudgetDiagnostic>();
        await using var host = await TestHost.CreateAsync(provider, new RecordingTool(ToolEffect.ReadOnly),
            new RecordingObserver(), TestSettingsStore.Create(40000), enableSummary: true,
            summarizer: summaries, context: new ReuseContext(28500), contextDiagnostics: diagnostics.Add);
        var stages = new List<ConversationRequestStage>();
        host.Orchestrator.Updated += update =>
        {
            if (update.RequestStage is { } stage) stages.Add(stage);
        };
        var result = await host.Orchestrator.SendAsync(RoleId, "hello", default, new(ProjectId));
        Assert.Equal(ConversationSendStatus.Completed, result.Status);
        Assert.Contains(diagnostics, d => d.Stage == ContextDiagnosticStage.BeforeCompaction
            && d.MeasuredInput >= d.TriggerTokens);
        Assert.Empty(summaries.Requests);
        Assert.DoesNotContain(ConversationRequestStage.Compacting, stages);
    }

    private static void SeedReusableHistory(TestHost host)
    {
        var key = new ContentContextKey(RoleId, "test-package", "1.0.0", "default", "persona-v1", "knowledge-v1");
        var now = DateTimeOffset.UnixEpoch;
        host.Conversations.CreateConversation("reuse-conversation", RoleId, key, now, ProjectId);
        for (var sequence = 1; sequence <= 8; sequence++)
            host.Conversations.Append(new("reuse-" + sequence, "reuse-conversation", RoleId,
                sequence % 2 == 1 ? ChatMessageRole.User : ChatMessageRole.Assistant,
                $"old-source-{sequence}:" + new string('x', sequence <= 2 ? 10000 : 1600),
                ChatMessageStatus.Completed, now.AddSeconds(sequence), key, sequence));
        host.Conversations.WriteState("LastActiveConversationId:" + RoleId, "reuse-conversation", now);
    }

    private sealed class ReuseContext(int characters) : IAgentContextProvider
    {
        public string Id => "reuse-context";
        public bool ProvidesTrustedInstructions => false;
        public ValueTask<IReadOnlyList<AgentContextBlock>> BuildAsync(AgentContextRequest request, CancellationToken token) =>
            ValueTask.FromResult<IReadOnlyList<AgentContextBlock>>([new("records", AgentContextKind.Data,
                "fresh-reuse-context:" + new string('m', characters), 100)]);
    }

    private sealed class ReuseSummaryRecorder : IConversationSummarizer
    {
        public List<ChatRequest> Requests { get; } = [];
        public Action? Starting { get; set; }
        public bool Invalid { get; init; }
        public Task<SummaryAttempt> SummarizeAsync(IChatProvider provider, ChatRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Requests.Add(request);
            Starting?.Invoke();
            token.ThrowIfCancellationRequested();
            if (Invalid) return Task.FromResult(new SummaryAttempt("incomplete", "length", null));
            var instructions = request.Messages[0].Text;
            var last = instructions[(instructions.LastIndexOf('：') + 1)..].Trim();
            return Task.FromResult(new SummaryAttempt("任务目标：继续测试\n用户已确认的决定：未确定\n否定、更正及被替代事项：未确定\n尚未解决的问题：未确定\n下一步：继续\n来源消息：" + last,
                "stop", null));
        }
    }
}
