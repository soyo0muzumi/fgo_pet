using System.Text.Json;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Settings;
using FgoPet.Dialogue.Settings;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Providers;
using FgoPet.Kernel.Agent;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

public sealed partial class NativeConversationIntegrationTests
{
    [Fact]
    public async Task Ordinary_read_batch_returns_every_result_before_completing()
    {
        var provider = new ScriptedProvider(OrdinaryReadBatch("read-1", "read-2"), ProviderTurn.Final("Both observations received."));
        var tool = new RecordingTool(ToolEffect.ReadOnly);
        await using var host = await OrdinaryHost(provider, tool);
        var result = await host.Orchestrator.SendAsync(RoleId, "Read both synthetic observations.", default, new(ProjectId));
        Assert.Equal(ConversationSendStatus.Completed, result.Status);
        Assert.Equal(2, tool.Invocations.Count);
        Assert.Equal(2, provider.Requests.Count);
        var transcript = provider.Requests[1].ModelMessages;
        ModelProtocol.ValidateTranscript(transcript);
        Assert.Equal(2, Assert.Single(transcript.Where(m => !m.ToolCalls.IsEmpty)).ToolCalls.Length);
        Assert.Equal(new[] { "read-1", "read-2" }, transcript.Where(m => m.Role == ModelMessageRole.Tool).Select(m => m.ToolCallId));
        Assert.Equal("Both observations received.", host.Conversations.LoadMessages(result.ConversationId, RoleId)[^1].Text);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Invalid_read_batch_is_rejected_before_any_member_executes(bool duplicateIdentity)
    {
        var provider = new ScriptedProvider(OrdinaryReadBatch("read-1", duplicateIdentity ? "read-1" : "read-2",
            duplicateIdentity ? "{}" : "[1]"));
        var tool = new RecordingTool(ToolEffect.ReadOnly);
        await using var host = await OrdinaryHost(provider, tool);
        var result = await host.Orchestrator.SendAsync(RoleId, "Read both.", default, new(ProjectId));
        Assert.Equal(ConversationSendStatus.Failed, result.Status);
        Assert.Empty(tool.Invocations);
        Assert.Single(provider.Requests);
    }

    [Fact]
    public async Task Mixed_read_and_proposal_batch_keeps_the_existing_whole_batch_rejection()
    {
        var proposal = new OrdinaryProposalProbe();
        var read = new RecordingTool(ToolEffect.ReadOnly);
        var provider = new ScriptedProvider(new ProviderTurn([
            new("", ToolCallDelta: new(0, id: "read-1", name: ModelToolNameMap.GetWireName("fixture.read"), argumentsDelta: "{}")),
            new("", ToolCallDelta: new(1, id: "proposal-1", name: ModelToolNameMap.GetWireName("fixture.propose"), argumentsDelta: "{}")),
            new("", IsComplete: true, FinishReason: "tool_calls")]));
        await using var host = await OrdinaryHost(provider, read, extraTools: [proposal]);
        var updates = new List<ConversationUpdate>();
        host.Orchestrator.Updated += updates.Add;
        var result = await host.Orchestrator.SendAsync(RoleId, "Do not create a draft.", default, new(ProjectId));
        Assert.Equal(ConversationSendStatus.Completed, result.Status);
        Assert.Empty(read.Invocations);
        Assert.Equal(0, proposal.Calls);
        Assert.Single(provider.Requests);
        Assert.Contains(updates, u => u.Type == ConversationUpdateType.AssistantCompleted && u.ToolOutcome == CapabilityOutcome.InvalidToolCall);
    }

    [Fact]
    public async Task Ordinary_read_result_reaches_the_model_before_a_completed_answer_is_saved()
    {
        var provider = new ScriptedProvider(ProviderTurn.Call("fixture.read", "read-1", "{}"),
            ProviderTurn.Final("The synthetic configuration says 2."));
        var tool = new RecordingTool(ToolEffect.ReadOnly, "synthetic-max-retries=2");
        await using var host = await OrdinaryHost(provider, tool);

        var result = await host.Orchestrator.SendAsync(RoleId, "Read the synthetic configuration.", default, new(ProjectId));

        Assert.Equal(ConversationSendStatus.Completed, result.Status);
        Assert.Single(tool.Invocations);
        Assert.Equal(2, provider.Requests.Count);
        var continuation = provider.Requests[1];
        ModelProtocol.ValidateTranscript(continuation.ModelMessages);
        var call = Assert.Single(continuation.ModelMessages.SelectMany(message => message.ToolCalls));
        var observation = Assert.Single(continuation.ModelMessages.Where(message => message.Role == ModelMessageRole.Tool));
        Assert.Equal(call.CallId, observation.ToolCallId);
        Assert.Contains("synthetic-max-retries=2", observation.Content);
        Assert.Equal(2048, continuation.MaxOutputTokens);
        Assert.Equal(provider.Requests[0].Tools!.Select(t => t.Name), continuation.Tools!.Select(t => t.Name));
        var saved = host.Conversations.LoadMessages(result.ConversationId, RoleId);
        Assert.Equal("The synthetic configuration says 2.", saved[^1].Text);
        Assert.DoesNotContain(saved, message => message.Text == "[工具调用]");
    }

    [Fact]
    public async Task Ordinary_read_chain_keeps_each_call_result_pair_and_resolves_only_offered_wire_names()
    {
        var provider = new ScriptedProvider(ProviderTurn.Call("fixture.read", "read-1", "{}"),
            ProviderTurn.Call("fixture.read", "read-2", "{}"), ProviderTurn.Final("Two observations received."));
        var tool = new RecordingTool(ToolEffect.ReadOnly);
        await using var host = await OrdinaryHost(provider, tool);
        var result = await host.Orchestrator.SendAsync(RoleId, "Read twice.", default, new(ProjectId));
        Assert.Equal(ConversationSendStatus.Completed, result.Status);
        Assert.Equal(2, tool.Invocations.Count);
        Assert.Equal(3, provider.Requests.Count);
        var messages = provider.Requests[^1].ModelMessages;
        ModelProtocol.ValidateTranscript(messages);
        Assert.Equal(new[] { "read-1", "read-2" }, messages.Where(m => m.Role == ModelMessageRole.Tool).Select(m => m.ToolCallId));
    }

    [Fact]
    public async Task Duplicate_read_call_id_fails_before_the_second_tool_execution()
    {
        var provider = new ScriptedProvider(ProviderTurn.Call("fixture.read", "read-1", "{}"),
            ProviderTurn.Call("fixture.read", "read-1", "{}"));
        var tool = new RecordingTool(ToolEffect.ReadOnly);
        await using var host = await OrdinaryHost(provider, tool);
        var result = await host.Orchestrator.SendAsync(RoleId, "Read twice.", default, new(ProjectId));
        Assert.Equal(ConversationSendStatus.Failed, result.Status);
        Assert.Single(tool.Invocations);
        Assert.Equal(2, provider.Requests.Count);
    }

    [Fact]
    public async Task Explicit_host_grant_allows_the_answer_after_three_read_requests()
    {
        var provider = new ScriptedProvider(ProviderTurn.Call("fixture.read", "read-1", "{}"),
            ProviderTurn.Call("fixture.read", "read-2", "{}"), ProviderTurn.Call("fixture.read", "read-3", "{}"),
            ProviderTurn.Final("All three observations were received."));
        var tool = new RecordingTool(ToolEffect.ReadOnly);
        await using var host = await OrdinaryHost(new HostGrantedProvider(provider), tool);
        var result = await host.Orchestrator.SendAsync(RoleId, "Read three observations.", default, new(ProjectId));
        Assert.Equal(ConversationSendStatus.Completed, result.Status);
        Assert.Equal(4, provider.Requests.Count);Assert.Equal(3, tool.Invocations.Count);
        Assert.Equal("All three observations were received.", host.Conversations.LoadMessages(result.ConversationId, RoleId)[^1].Text);
    }

    [Fact]
    public async Task Explicit_host_call_grant_preserves_cancellation_before_continuation()
    {
        using var cancelled = new CancellationTokenSource();
        var provider = new ScriptedProvider(ProviderTurn.Call("fixture.read", "read-1", "{}"), ProviderTurn.Final("Must not continue."));
        await using var host = await OrdinaryHost(new HostGrantedProvider(provider), new CancellingOrdinaryRead(cancelled));
        var result = await host.Orchestrator.SendAsync(RoleId, "Read once.", cancelled.Token, new(ProjectId));
        Assert.Equal(ConversationSendStatus.Cancelled, result.Status);Assert.Single(provider.Requests);
    }

    [Fact]
    public async Task Ordinary_read_chain_keeps_the_existing_three_primary_attempt_bound()
    {
        var provider = new ScriptedProvider(ProviderTurn.Call("fixture.read", "read-1", "{}"),
            ProviderTurn.Call("fixture.read", "read-2", "{}"), ProviderTurn.Call("fixture.read", "read-3", "{}"),
            ProviderTurn.Final("Must not send a fourth primary request."));
        await using var host = await OrdinaryHost(provider, new RecordingTool(ToolEffect.ReadOnly));
        var result = await host.Orchestrator.SendAsync(RoleId, "Keep reading.", default, new(ProjectId));
        Assert.Equal(ConversationSendStatus.Failed, result.Status);
        Assert.Equal(3, provider.Requests.Count);
    }

    [Fact]
    public async Task Oversized_read_result_fails_before_the_continuation_provider_is_entered()
    {
        var provider = new ScriptedProvider(ProviderTurn.Call("fixture.read", "read-1", "{}"),
            ProviderTurn.Final("Must not receive an over-budget request."));
        var tool = new RecordingTool(ToolEffect.ReadOnly, new string('x', 20000));
        await using var host = await OrdinaryHost(new LimitedProvider(provider, 16000), tool);
        var result = await host.Orchestrator.SendAsync(RoleId, "Read the large synthetic object.", default, new(ProjectId));
        Assert.Equal(ConversationSendStatus.Failed, result.Status);
        Assert.Single(tool.Invocations);
        Assert.Single(provider.Requests);
        Assert.DoesNotContain(host.Conversations.LoadMessages(result.ConversationId, RoleId),
            message => message.Role == ChatMessageRole.Assistant && message.Status == ChatMessageStatus.Completed);
    }

    [Fact]
    public async Task Cancellation_after_read_never_sends_a_continuation()
    {
        using var cancellation = new CancellationTokenSource();
        var provider = new ScriptedProvider(ProviderTurn.Call("fixture.read", "read-1", "{}"));
        var tool = new CancellingOrdinaryRead(cancellation);
        await using var host = await OrdinaryHost(provider, tool);
        var result = await host.Orchestrator.SendAsync(RoleId, "Read once.", cancellation.Token, new(ProjectId));
        Assert.Equal(ConversationSendStatus.Cancelled, result.Status);
        Assert.Single(provider.Requests);
        Assert.Equal(1, tool.Calls);
    }

    [Fact]
    public async Task Rejected_read_continuation_neither_replays_the_tool_nor_downgrades_the_catalog()
    {
        var provider = new ScriptedProvider(ProviderTurn.Call("fixture.read", "read-1", "{}"),
            ProviderTurn.Fail(new ProviderRequestException(ProviderFailureCategory.ToolsRejected, "synthetic rejection")));
        var tool = new RecordingTool(ToolEffect.ReadOnly);
        await using var host = await OrdinaryHost(provider, tool);
        var result = await host.Orchestrator.SendAsync(RoleId, "Read once.", default, new(ProjectId));
        Assert.Equal(ConversationSendStatus.Failed, result.Status);
        Assert.Single(tool.Invocations);
        Assert.Equal(2, provider.Requests.Count);
        Assert.True(host.Settings.Load().ModelConnection!.ToolsSupported);
    }

    [Fact]
    public async Task DeepSeek_read_continuation_echoes_and_measures_transient_reasoning_without_persisting_it()
    {
        const string reasoning = "private-synthetic-reasoning";
        var first = new ProviderTurn([new("", ReasoningDelta: reasoning),
            new("", ToolCallDelta: new(0, id: "read-1", name: "fixture.read", argumentsDelta: "{}")),
            new("", IsComplete: true, FinishReason: "tool_calls")]);
        var provider = new ScriptedProvider(first, ProviderTurn.Final("Observed."));
        await using var host = await OrdinaryHost(new NamedOrdinaryProvider(provider), new RecordingTool(ToolEffect.ReadOnly), deepSeek: true);
        var result = await host.Orchestrator.SendAsync(RoleId, "Read once.", default, new(ProjectId));
        Assert.Equal(ConversationSendStatus.Completed, result.Status);
        var request = provider.Requests[1];
        Assert.Contains("\"reasoning_content\":\"" + reasoning + "\"", ChatRequestInputEnvelope.Write("deepseek-flash", request));
        Assert.DoesNotContain(reasoning, JsonSerializer.Serialize(request.ModelMessages));
        Assert.DoesNotContain(host.Conversations.LoadMessages(result.ConversationId, RoleId), m => m.Text.Contains(reasoning, StringComparison.Ordinal));
        var withoutTransient = ChatRequest.CreateAgent(request.ServantId, request.ConversationId,
            [.. request.ModelMessages.Select(m => m with { })], request.ContentContext, tools: request.Tools,
            toolChoice: request.ToolChoice, maxOutputTokens: request.MaxOutputTokens);
        var meter = new RequestTokenMeter();
        var route = ModelRouteKey.From(host.Settings.Load().ModelConnection!);
        Assert.True(meter.Measure(route, request).InputTokens > meter.Measure(route, withoutTransient).InputTokens);
        Assert.NotEqual(meter.Measure(route, request).Fingerprint, meter.Measure(route, withoutTransient).Fingerprint);
    }

    private static ProviderTurn OrdinaryReadBatch(string firstId, string secondId, string secondArguments = "{}") => new([
        new("", ToolCallDelta: new(0, id: firstId, name: ModelToolNameMap.GetWireName("fixture.read"), argumentsDelta: "{}")),
        new("", ToolCallDelta: new(1, id: secondId, name: ModelToolNameMap.GetWireName("fixture.read"), argumentsDelta: secondArguments)),
        new("", IsComplete: true, FinishReason: "tool_calls")]);

    private static Task<TestHost> OrdinaryHost(IChatProvider provider, IToolProvider tool, bool deepSeek = false,
        IReadOnlyList<IToolProvider>? extraTools = null)
    {
        var settings = new TestSettingsStore(DialogueSettings.Defaults with {
            ModelConnection = new ModelConnectionSettings(provider.ProviderId,
                deepSeek ? "https://api.deepseek.com/v1" : "https://fixture.invalid/v1", provider.ModelId,
                toolsSupported: true, contextWindowOverride: 65536, maxOutputTokens: 2048)
        });
        return TestHost.CreateAsync(provider, tool, new RecordingObserver(), settings: settings, useNative: false, extraTools: extraTools);
    }

    private sealed class CancellingOrdinaryRead(CancellationTokenSource cancellation) : IToolProvider
    {
        public int Calls { get; private set; }
        public ToolDescriptor Descriptor { get; } = new("fixture.read", "Synthetic cancellable observation.", "{\"type\":\"object\"}", ToolEffect.ReadOnly);
        public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken token)
        {
            Calls++;
            cancellation.Cancel();
            return ValueTask.FromResult(new ToolResult(true, JsonSerializer.SerializeToElement(new { value = "synthetic" })));
        }
    }

    private sealed class HostGrantedProvider(IChatProvider inner) : IChatProvider, IChatRequestLimit
    {
        public string ProviderId=>inner.ProviderId;public string ModelId=>inner.ModelId;
        public int? MaxRequestsPerTurn=>null;
        public IAsyncEnumerable<ChatStreamChunk> StreamAsync(ChatRequest request,CancellationToken token)=>inner.StreamAsync(request,token);
        public Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken token)=>inner.ListModelsAsync(token);
    }
    private sealed class NamedOrdinaryProvider(IChatProvider inner) : IChatProvider
    {
        public string ProviderId => "deepseek";
        public string ModelId => "deepseek-flash";
        public IAsyncEnumerable<ChatStreamChunk> StreamAsync(ChatRequest request, CancellationToken token) => inner.StreamAsync(request, token);
        public Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken token) => inner.ListModelsAsync(token);
    }

    private sealed class OrdinaryProposalProbe : IToolProvider
    {
        public int Calls { get; private set; }
        public ToolDescriptor Descriptor { get; } = new("fixture.propose", "Synthetic proposal.", "{\"type\":\"object\"}", ToolEffect.Proposal);
        public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken token)
        {
            Calls++;
            return ValueTask.FromResult(new ToolResult(true, JsonSerializer.SerializeToElement(new { draft = "synthetic" })));
        }
    }
}
