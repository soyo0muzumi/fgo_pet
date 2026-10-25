using FgoPet.Core.Dialogue;
using System.Text.Json;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Providers;
using FgoPet.Kernel.Agent;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

public sealed partial class NativeConversationIntegrationTests
{
    private static ProviderTurn InvalidHandoff(bool sibling = false)
    {
        var chunks = ProviderTurn.Call(FinalizeTool, "invalid-control", "{}").Chunks.Take(1).ToList();
        chunks.Add(sibling ? new ChatStreamChunk("", ToolCallDelta: new(1, id: "forbidden-business",
            name: ModelToolNameMap.GetWireName("fixture.read"), argumentsDelta: "{}"))
            : new ChatStreamChunk("untrusted-rejected-draft"));
        chunks.Add(new ChatStreamChunk("", IsComplete: true, FinishReason: "tool_calls"));
        return new(chunks);
    }

    private static void AssertControlRepair(ChatRequest request)
    {
        Assert.Equal(ModelToolNameMap.GetWireName(FinalizeTool), Assert.Single(request.Tools!).Name);
        Assert.DoesNotContain(request.ModelMessages, m => m.Content.Contains("untrusted-rejected-draft", StringComparison.Ordinal)
            || m.ToolCalls.Any(c => c.CallId is "invalid-control" or "forbidden-business"));
        Assert.Contains(request.ModelMessages, m => m.Role == ModelMessageRole.System && m.Content.Contains("Correct the control signal once", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Finalize_repair_only_offers_control_preserves_observations_and_never_replays_business(bool sibling)
    {
        var provider = new ScriptedProvider(ProviderTurn.Call("fixture.read", "read-once", "{}"), InvalidHandoff(sibling),
            ProviderTurn.Call(FinalizeTool, "corrected-control", "{}"),
            ProviderTurn.Final("{\"text\":\"verified answer\",\"emotion\":\"neutral\"}")) { RawFinalResponses = true, AutoFinalize = false };
        var tool = new RecordingTool(ToolEffect.ReadOnly);
        var observer = new RecordingObserver();
        await using var host = await TestHost.CreateAsync(provider, tool, observer);
        var result = await host.Orchestrator.SendAsync(RoleId, "Read and answer.", default, new(ProjectId));
        Assert.Equal(ConversationSendStatus.Completed, result.Status);
        Assert.Equal(4, provider.Requests.Count);
        AssertControlRepair(provider.Requests[2]);
        Assert.Contains(provider.Requests[2].ModelMessages, m => m.Role == ModelMessageRole.Tool && m.ToolCallId == "read-once");
        AssertFinalRequest(provider.Requests[3]);
        Assert.Single(tool.Invocations);
        Assert.Equal("verified answer", Assert.Single(observer.Turns).AssistantText);
        var run = (await host.Runs.LoadAsync(result.RunId!, default))!;
        Assert.Equal(4, run.Snapshot.ModelRequests);
        Assert.Equal(2, run.Snapshot.ToolCalls);
        Assert.DoesNotContain(run.CompletedSteps.SelectMany(s => s.Calls), c => c.Call.CallId == "invalid-control");
    }

    [Theory]
    [InlineData("body")]
    [InlineData("sibling")]
    [InlineData("arguments")]
    [InlineData("business")]
    [InlineData("plain")]
    public async Task Finalize_repair_invalid_correction_stops_after_one_attempt_without_effects(string scenario)
    {
        var correction = scenario switch {
            "body" => InvalidHandoff(), "sibling" => InvalidHandoff(true),
            "arguments" => ProviderTurn.Call(FinalizeTool, "bad", "{\"text\":\"rejected\"}"),
            "business" => ProviderTurn.Call("fixture.read", "bad", "{}"),
            _ => ProviderTurn.Final("must not become an answer") };
        var provider = new ScriptedProvider(InvalidHandoff(), correction, ProviderTurn.Call(FinalizeTool, "unused", "{}"))
            { RawFinalResponses = true, AutoFinalize = false };
        var tool = new RecordingTool(ToolEffect.ReadOnly);
        var observer = new RecordingObserver();
        await using var host = await TestHost.CreateAsync(provider, tool, observer);
        var result = await host.Orchestrator.SendAsync(RoleId, "Answer.", default, new(ProjectId));
        Assert.Equal(ConversationSendStatus.Failed, result.Status);
        Assert.Equal("MODEL_INVALID_FINALIZE", (await host.Runs.LoadAsync(result.RunId!, default))!.Snapshot.ErrorCode);
        Assert.Equal(2, provider.Requests.Count);
        AssertControlRepair(provider.Requests[1]);
        Assert.Empty(tool.Invocations);
        Assert.Empty(observer.Turns);
    }

    [Theory]
    [InlineData(ProviderFailureCategory.Network)]
    [InlineData(ProviderFailureCategory.ServiceUnavailable)]
    [InlineData(ProviderFailureCategory.ToolsRejected)]
    public async Task Finalize_repair_transport_failure_never_retries_or_downgrades(ProviderFailureCategory category)
    {
        var provider = new ScriptedProvider(InvalidHandoff(), ProviderTurn.Fail(new(category, "synthetic failure")),
            ProviderTurn.Call(FinalizeTool, "unused", "{}")) { RawFinalResponses = true, AutoFinalize = false };
        var tool = new RecordingTool(ToolEffect.ReadOnly);
        var observer = new RecordingObserver();
        await using var host = await TestHost.CreateAsync(provider, tool, observer);
        Assert.Equal(ConversationSendStatus.Failed, (await host.Orchestrator.SendAsync(RoleId, "Answer.", default, new(ProjectId))).Status);
        Assert.Equal(2, provider.Requests.Count);
        AssertControlRepair(provider.Requests[1]);
        Assert.Empty(tool.Invocations);
        Assert.Empty(observer.Turns);
    }

    [Fact]
    public async Task Finalize_repair_cancellation_never_delivers_or_replays()
    {
        using var cancel = new CancellationTokenSource();
        var provider = new ScriptedProvider(InvalidHandoff(), ProviderTurn.Call(FinalizeTool, "corrected", "{}"))
            { RawFinalResponses = true, AutoFinalize = false };
        provider.RequestReceived = _ => { if (provider.Requests.Count == 2) cancel.Cancel(); };
        var observer = new RecordingObserver();
        var tool = new RecordingTool(ToolEffect.ReadOnly);
        await using var host = await TestHost.CreateAsync(provider, tool, observer);
        Assert.Equal(ConversationSendStatus.Cancelled, (await host.Orchestrator.SendAsync(RoleId, "Answer.", cancel.Token, new(ProjectId))).Status);
        Assert.Equal(2, provider.Requests.Count);
        Assert.Empty(observer.Turns);
        Assert.Empty(tool.Invocations);
    }

    [Fact]
    public async Task Finalize_repair_retains_current_image_reference_without_recapturing()
    {
        var script = new ScriptedProvider(ProviderTurn.Call("fixture.read", "capture-once", "{}"), InvalidHandoff(),
            ProviderTurn.Call(FinalizeTool, "corrected", "{}"),
            ProviderTurn.Final("{\"text\":\"synthetic visual answer\",\"emotion\":\"neutral\"}"))
            { RawFinalResponses = true, AutoFinalize = false };
        var image = new ToolImageReference(Guid.NewGuid().ToString("N"));
        var tool = new FinalizeImageTool(image);
        var settings = TestSettingsStore.Create(100000);
        var provider = new FinalizeImageProvider(script, image, ModelRouteKey.From(settings.Load().ModelConnection!));
        var observer = new RecordingObserver();
        await using var host = await TestHost.CreateAsync(provider, tool, observer, settings);
        Assert.Equal(ConversationSendStatus.Completed, (await host.Orchestrator.SendAsync(RoleId, "Read the synthetic image.", default, new(ProjectId))).Status);
        Assert.Equal(1, tool.Captures);
        Assert.Equal(4, script.Requests.Count);
        foreach (var request in script.Requests.Skip(1))
            Assert.Equal(image, Assert.Single(request.ModelMessages.Where(m => m.Image is not null)).Image);
        AssertControlRepair(script.Requests[2]);
        Assert.Single(observer.Turns);
    }

    // Structural Native-loop test; pixel decoding and provider serialization have separate tests.
    private sealed class FinalizeImageTool(ToolImageReference image) : IToolProvider
    {
        public int Captures { get; private set; }
        public ToolDescriptor Descriptor { get; } = new("fixture.read", "Synthetic screenshot reference", "{\"type\":\"object\"}", ToolEffect.ReadOnly);
        public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Captures++;
            return ValueTask.FromResult(new ToolResult(true, JsonSerializer.SerializeToElement(new { synthetic = true })) { Image = image });
        }
    }

    private sealed class FinalizeImageProvider(ScriptedProvider inner, ToolImageReference image, ModelRouteKey route)
        : IChatProvider, IChatImageInputProvider, IChatImageResolver
    {
        private readonly ChatImageInput pixels = new(Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="), 1, 1, isBrowserImage: true);
        public string ProviderId => inner.ProviderId;
        public string ModelId => inner.ModelId;
        public Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken token) => inner.ListModelsAsync(token);
        public IAsyncEnumerable<ChatStreamChunk> StreamAsync(ChatRequest request, CancellationToken token) => inner.StreamAsync(request, token);
        public void PrepareImages(ChatRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Assert.All(request.ModelMessages.Where(m => m.Image is not null), m => Assert.Equal(image, m.Image));
            ChatImageInputs.Prepare(request, route, this, true, token);
        }
        public ChatImageInput Resolve(ToolImageReference reference, string roleId, string conversationId, ModelRouteKey requestedRoute, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Assert.Equal(image, reference);
            Assert.Equal(RoleId, roleId);
            Assert.Equal(route, requestedRoute);
            return pixels;
        }
        public IDisposable AcquireDispatch(ToolImageReference reference, string roleId, string conversationId, ModelRouteKey requestedRoute, CancellationToken token)
            => throw new NotSupportedException("Synthetic provider does not dispatch HTTP.");
    }

    [Fact]
    public async Task Finalize_repair_never_spends_the_last_reserved_final_request()
    {
        var turns = Enumerable.Range(0, 30).Select(i => ProviderTurn.Call("fixture.read", "read-" + i, "{}"))
            .Append(InvalidHandoff()).Append(ProviderTurn.Call(FinalizeTool, "unused", "{}")).ToArray();
        var provider = new ScriptedProvider(turns) { RawFinalResponses = true, AutoFinalize = false };
        var tool = new RecordingTool(ToolEffect.ReadOnly);
        var observer = new RecordingObserver();
        await using var host = await TestHost.CreateAsync(provider, tool, observer, TestSettingsStore.Create(100000));
        var result = await host.Orchestrator.SendAsync(RoleId, "Read synthetic observations.", default, new(ProjectId));
        Assert.Equal(ConversationSendStatus.Failed, result.Status);
        Assert.Equal(31, provider.Requests.Count);
        Assert.Equal(30, tool.Invocations.Count);
        Assert.Empty(observer.Turns);
        Assert.Equal("MODEL_INVALID_FINALIZE", (await host.Runs.LoadAsync(result.RunId!, default))!.Snapshot.ErrorCode);
    }

    [Fact]
    public async Task Finalize_repair_still_requires_valid_final_json_and_allows_existing_final_format_repair()
    {
        var provider = new ScriptedProvider(InvalidHandoff(), ProviderTurn.Call(FinalizeTool, "corrected", "{}"),
            ProviderTurn.Final("bad final"), ProviderTurn.Final("{\"text\":\"verified answer\",\"emotion\":\"neutral\"}"))
            { RawFinalResponses = true, AutoFinalize = false };
        var observer = new RecordingObserver();
        await using var host = await TestHost.CreateAsync(provider, new RecordingTool(ToolEffect.ReadOnly), observer);
        Assert.Equal(ConversationSendStatus.Completed, (await host.Orchestrator.SendAsync(RoleId, "Answer.", default, new(ProjectId))).Status);
        Assert.Equal(4, provider.Requests.Count);
        Assert.All(provider.Requests.Skip(2), AssertFinalRequest);
        Assert.Equal("verified answer", Assert.Single(observer.Turns).AssistantText);
    }
}
