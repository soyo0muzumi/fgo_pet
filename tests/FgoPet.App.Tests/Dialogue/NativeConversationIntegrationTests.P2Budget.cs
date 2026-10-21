using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Settings;
using FgoPet.Dialogue.Settings;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.Infrastructure.Providers;
using FgoPet.Kernel.Agent;
using FgoPet.Testing.Persona;
using Xunit;
using BatchFixture = FgoPet.App.Tests.Dialogue.P2BatchProviderTests.BatchFixture;

namespace FgoPet.App.Tests.Dialogue;

public sealed partial class NativeConversationIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task P2_budget_wraps_real_primary_paths_and_measures_source_persona_envelopes(bool native)
    {
        using var batch = new BatchFixture();
        if (native) batch.Respond = (_, _) => Task.FromResult(P2TextResponse("{\"text\":\"fixture\",\"emotion\":\"neutral\"}"));
        var measured = new P2MeasuredProvider(batch.Batch);
        var binding = ResolveSourcePersona();
        var observer = new RecordingObserver();
        await using var host = await TestHost.CreateAsync(measured, new RecordingTool(ToolEffect.ReadOnly), observer,
            settings: P2Settings(), contentResolver: new BoundPersonaResolver(binding), useNative: native);
        foreach (var input in new[] { "合成验收：我暂时把讨论安排在周一。", "纠正一下，是周二；之前的周一作废。这只是本次对话，不要长期记住。" })
        {
            var result = await host.Orchestrator.SendAsync(SourcePersonaRole, input, default, new(ProjectId));
            Assert.Equal(ConversationSendStatus.Completed, result.Status);
            AssertPersonaRequest(measured.Requests.Last(), binding, native, input);
        }
        Assert.Equal(2, observer.Turns.Count);
        Assert.Equal(native ? 4 : 2, batch.Sends);
        Assert.Equal(native ? 11_200 : 5_600, batch.Batch.Snapshot.CommittedMicros);
        for (var index = batch.Sends; index < 20; index++) await batch.Send();
        var denied = await host.Orchestrator.SendAsync(SourcePersonaRole, "额度用尽后的合成请求。", default, new(ProjectId));
        Assert.Equal(ConversationSendStatus.Failed, denied.Status);
        Assert.Equal(20, batch.Sends);
        Assert.Equal(20, batch.Batch.Snapshot.Requests);
        WriteP2Measurements(native ? "native" : "ordinary", measured.Measurements.Take(2).ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task P2_budget_counts_native_tool_continuation_and_blocks_it_at_twenty(bool exhausted)
    {
        using var batch = new BatchFixture();
        if (exhausted) for (var index = 0; index < 19; index++) await batch.Send();
        var firstAttempt = batch.Sends + 1;
        var measured = new P2MeasuredProvider(batch.Batch);
        batch.Respond = (_, _) => Task.FromResult(batch.Sends == firstAttempt ? P2ToolResponse()
            : P2TextResponse("{\"text\":\"fixture\",\"emotion\":\"neutral\"}"));
        var tool = new RecordingTool(ToolEffect.ReadOnly);
        await using var host = await TestHost.CreateAsync(measured, tool, new RecordingObserver(), settings: P2Settings(),
            contentResolver: new BoundPersonaResolver(ResolveSourcePersona()));
        var result = await host.Orchestrator.SendAsync(SourcePersonaRole, "合成验收：读取本地夹具，再核对讨论日期。", default, new(ProjectId));
        Assert.Equal(exhausted ? ConversationSendStatus.Failed : ConversationSendStatus.Completed, result.Status);
        Assert.Single(tool.Invocations);
        Assert.Equal(exhausted ? 20 : 3, batch.Sends);
        Assert.Equal(batch.Sends, batch.Batch.Snapshot.Requests);
        if (!exhausted) WriteP2Measurements("native-tool", measured.Measurements.ToArray());
    }

    [Fact]
    public async Task P2_budget_blocks_native_tools_rejection_retry_when_first_request_cost_is_unknown()
    {
        using var batch = new BatchFixture();
        batch.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        { Content = new StringContent("{\"error\":{\"message\":\"fixture tools rejected\"}}") });
        var measured = new P2MeasuredProvider(batch.Batch);
        var tool = new RecordingTool(ToolEffect.ReadOnly);
        await using var host = await TestHost.CreateAsync(measured, tool, new RecordingObserver(), settings: P2Settings());
        var result = await host.Orchestrator.SendAsync(RoleId, "合成验收。", default, new(ProjectId));
        Assert.Equal(ConversationSendStatus.Failed, result.Status);
        Assert.True(measured.Requests.Count >= 2); // Runtime attempted its ordinary no-tools fallback.
        Assert.Equal(1, batch.Sends); // Fallback never reached the terminal HTTP transport.
        Assert.Equal(2_113_536, batch.Batch.Snapshot.CommittedMicros);
        Assert.Equal("P2_USAGE_UNKNOWN", batch.Batch.Snapshot.StopCode);
        Assert.Empty(tool.Invocations);
    }

    [Fact]
    public async Task P2_budget_is_shared_by_real_summary_and_history_recall_owners()
    {
        using var batch = new BatchFixture();
        var measured = new P2MeasuredProvider(batch.Batch);
        var summaryRequest = new ChatRequest("fixture", "current", [new(ChatMessageRole.User, "合成摘要输入")],
            metadata: new Dictionary<string, string> { ["fgo_auxiliary"] = "context_summary" }, maxOutputTokens: 512);
        var summary = await new ProviderConversationSummarizer().SummarizeAsync(measured, summaryRequest, default);
        Assert.Equal("fixture", summary.Text);
        Assert.Equal(1, batch.Batch.Snapshot.Requests);
        Assert.Equal(2_800, batch.Batch.Snapshot.CommittedMicros);
        batch.Respond = (_, _) => Task.FromResult(P2TextResponse("{\"queries\":[],\"selected\":[],\"ambiguous\":false}"));
        var recall = new ConversationRecallService(new P2RecallRepository(), new P2Resolver(measured), P2Settings(),
            new ModelContextResolver(_ => measured), new RequestTokenMeter());
        var result = await recall.RetrieveAsync(new("fixture", ProjectId), "current", "继续昨天的计划", default);
        Assert.Equal(RecallStatus.Empty, result.Status);
        Assert.Equal(2, batch.Batch.Snapshot.Requests);
        Assert.Equal(5_600, batch.Batch.Snapshot.CommittedMicros);
        Assert.Equal(new[] { "context_summary", "history_recall" }, measured.Requests.Select(r => r.Metadata["fgo_auxiliary"]));
        for (var index = 2; index < 20; index++) await batch.Send();
        Assert.Equal("P2_REQUEST_LIMIT", (await Assert.ThrowsAsync<P2BudgetException>(() =>
            new ProviderConversationSummarizer().SummarizeAsync(measured, summaryRequest, default))).Code);
        Assert.Equal(20, batch.Sends);
        WriteP2Measurements("auxiliary", measured.Measurements.Take(2).ToArray());
    }

    private static TestSettingsStore P2Settings() => new(DialogueSettings.Defaults with
    {
        ModelConnection = new("deepseek", "https://api.deepseek.com/v1", P2BatchPolicy.Model, maxOutputTokens: 2048),
    });
    private static HttpResponseMessage P2TextResponse(string text) => P2BatchProviderTests.Response("data: " + JsonSerializer.Serialize(new
    {
        choices = new[] { new { delta = new { content = text }, finish_reason = "stop" } },
        usage = new { prompt_tokens = 1000, completion_tokens = 100 },
    }) + "\n\ndata: [DONE]\n\n");
    private static HttpResponseMessage P2ToolResponse() => P2ToolResponse("p2-local-read");
    private static HttpResponseMessage P2ToolResponse(string callId) => P2BatchProviderTests.Response("data: " + JsonSerializer.Serialize(new
    {
        choices = new[] { new { delta = new { tool_calls = new[] { new { index = 0, id = callId, type = "function",
            function = new { name = ModelToolNameMap.GetWireName("fixture.read"), arguments = "{}" } } } }, finish_reason = "tool_calls" } },
        usage = new { prompt_tokens = 1000, completion_tokens = 100 },
    }) + "\n\ndata: [DONE]\n\n");

    private sealed class P2MeasuredProvider(IChatProvider inner, string endpoint = "https://api.deepseek.com/v1",
        Action<ChatRequest>? beforeDispatch = null, Action<string>? finalObserved = null) : IChatProvider
    {
        public string ProviderId => inner.ProviderId;
        public string ModelId => inner.ModelId;
        public List<ChatRequest> Requests { get; } = [];
        public List<object> Measurements { get; } = [];
        public Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken token) => inner.ListModelsAsync(token);
        public async IAsyncEnumerable<ChatStreamChunk> StreamAsync(ChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        {
            beforeDispatch?.Invoke(request);
            Requests.Add(request);
            var route = ModelRouteKey.From(new ModelConnectionSettings(ProviderId, endpoint, ModelId));
            var measured = new RequestTokenMeter().Measure(route, request);
            Measurements.Add(new { request.IsAgentRequest, InputEstimate = measured.InputTokens, Kind = measured.Kind.ToString(),
                request.MaxOutputTokens, measured.Fingerprint });
            var final = finalObserved is null ? null : new System.Text.StringBuilder();
            var sawTool = false;
            await foreach (var chunk in inner.StreamAsync(request, token))
            {
                final?.Append(chunk.TextDelta);
                sawTool |= chunk.ToolCallDelta is not null;
                if (chunk.IsComplete && chunk.FinishReason == "stop" && !sawTool && final is not null)
                    finalObserved!(final.ToString()); // The caller records hashes/shape only, never this text.
                yield return chunk;
            }
        }
    }
    private static void WriteP2Measurements(string scenario, object[] measurements)
    {
        var directory = Environment.GetEnvironmentVariable("FGO_PET_P2_BUDGET_EVIDENCE");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, scenario + ".json"), JsonSerializer.Serialize(new
        { Scenario = scenario, Phase = "offline-envelope-measurement", Result = "passed", RealProviderDispatches = 0,
            PriceVersion = P2BatchPolicy.PriceVersion, At = DateTimeOffset.UtcNow, Measurements = measurements }));
    }
    private sealed class P2Resolver(IChatProvider provider) : IChatProviderResolver { public IChatProvider Resolve() => provider; }
    private sealed class P2RecallRepository : IConversationRecallRepository
    {
        public IReadOnlyList<HistoryHit> Browse(ConversationScope scope, string excludedConversationId, int limit = 10) =>
            [new(new("old-a", "message-a", 1, DateTimeOffset.UnixEpoch), "合成候选甲", "片段甲", false),
             new(new("old-b", "message-b", 1, DateTimeOffset.UnixEpoch), "合成候选乙", "片段乙", false)];
        public IReadOnlyList<HistoryHit> Discover(ConversationScope scope, string excludedConversationId, string query, int limit = 20) => [];
        public HistoryReadPage Read(ConversationScope scope, HistoryAnchor anchor, int maxChars = 6000, HistoryReadCursor? cursor = null) => new([], null);
    }
}
