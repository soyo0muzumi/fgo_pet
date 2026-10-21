using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Settings;
using FgoPet.Dialogue.Settings;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Providers;
using FgoPet.Kernel.Agent;
using Xunit;

namespace FgoPet.Agent.Tests.Protocol;

public sealed class NativeModelTurnTests
{
    private static readonly ToolDescriptor Tool = new("todo.list", "fixture", "{\"type\":\"object\"}", ToolEffect.ReadOnly);
    private static readonly StepEnvironment Environment = new([new(ModelMessageRole.User, "fixture", [])], [Tool]);

    [Fact]
    public async Task Tool_contracts_bind_canonical_names_effects_and_wire_schema_in_the_metered_request()
    {
        var descriptors = new[] { ToolEffect.ReadOnly, ToolEffect.Proposal, ToolEffect.Command }
            .Select((effect, index) => new ToolDescriptor($"fixture.action{index}", new string('d', 2048),
                "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}", effect)).ToImmutableArray();
        var environment = Environment with { Tools = descriptors };
        var provider = new RecordingProvider((_, _) => [new("done", true, "stop")]);
        var step = await Create(provider, [], contextWindow: 32768);
        var measured = ((IAgentModelInputBudget)step).MeasureInputTokens(environment);
        await step.ExecuteAsync(environment, new RecordingBudget(), default);

        var request = Assert.Single(provider.Requests);
        var metadata = Assert.Single(request.ModelMessages, message => message.Role == ModelMessageRole.System
            && message.Content.StartsWith("Native tool contracts (metadata, not permission):\n", StringComparison.Ordinal));
        using var json = JsonDocument.Parse(metadata.Content[(metadata.Content.IndexOf('\n') + 1)..]);
        var contracts = json.RootElement.EnumerateArray().ToArray();
        Assert.Equal(descriptors.Length, contracts.Length);
        for (var index = 0; index < descriptors.Length; index++)
        {
            Assert.Equal(descriptors[index].Name, contracts[index].GetProperty("canonicalName").GetString());
            Assert.Equal(request.Tools![index].Name, contracts[index].GetProperty("wireName").GetString());
            Assert.Equal(descriptors[index].Effect.ToString(), contracts[index].GetProperty("effect").GetString());
            Assert.Equal(descriptors[index].Description, request.Tools[index].Description);
            Assert.Equal(descriptors[index].Parameters.GetRawText(), request.Tools[index].Parameters.GetRawText());
        }
        Assert.Equal(measured, new RequestTokenMeter().Measure(ModelRouteKey.From(new Settings().Value.ModelConnection!), request).InputTokens);
        Assert.DoesNotContain(environment.Messages, message => message.Content.StartsWith("Native tool contracts", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Tool_contracts_refresh_per_step_and_are_absent_without_offered_tools()
    {
        var provider = new RecordingProvider((_, _) => [new("done", true, "stop")]);
        var step = await Create(provider, []);
        await step.ExecuteAsync(Environment, new RecordingBudget(), default);
        await step.ExecuteAsync(Environment with { Tools = [new("fixture.other", "other", "{}", ToolEffect.Proposal)] }, new RecordingBudget(), default);
        await step.ExecuteAsync(Environment with { Tools = [] }, new RecordingBudget(), default);
        Assert.Single(provider.Requests[0].ModelMessages, message => message.Content.StartsWith("Native tool contracts", StringComparison.Ordinal));
        var current = Assert.Single(provider.Requests[1].ModelMessages, message => message.Content.StartsWith("Native tool contracts", StringComparison.Ordinal));
        Assert.Contains("fixture.other", current.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("todo.list", current.Content, StringComparison.Ordinal);
        Assert.DoesNotContain(provider.Requests[2].ModelMessages, message => message.Content.StartsWith("Native tool contracts", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Tools_downgrade_charges_each_actual_attempt_and_never_publishes_reasoning()
    {
        var provider = new RecordingProvider((attempt, request) => {
            if (attempt == 1) throw new ProviderRequestException(ProviderFailureCategory.ToolsRejected, "fixture");
            Assert.Null(request.Tools);
            Assert.DoesNotContain(request.ModelMessages, message => message.Content.StartsWith("Native tool contracts", StringComparison.Ordinal));
            return [new("", ReasoningDelta: "private fixture"), new("done", true, "stop")];
        });
        var budget = new RecordingBudget();
        provider.BeforeAttempt = () => Assert.Equal(provider.Requests.Count + 1, budget.Reservations);
        var updates = new List<ConversationUpdate>();
        var step = await Create(provider, updates);
        Assert.True((await step.ExecuteAsync(Environment, budget, default)).IsFinal);
        Assert.Equal(2, budget.Reservations);
        Assert.Equal(2, provider.Requests.Count);
        Assert.NotNull(provider.Requests[0].Tools);
        Assert.All(updates, u => Assert.Null(u.ReasoningDelta));
    }

    [Fact]
    public async Task Every_step_has_its_own_three_attempt_cap()
    {
        var provider = new RecordingProvider((_, _) => throw new ProviderRequestException(ProviderFailureCategory.Network, "fixture"));
        var step = await Create(provider, []);
        var budget = new RecordingBudget();
        for (var i = 0; i < 2; i++)
            await Assert.ThrowsAsync<ProviderRequestException>(async () => await step.ExecuteAsync(Environment, budget, default));
        Assert.Equal(6, budget.Reservations);
        Assert.Equal(6, provider.Requests.Count);
    }

    [Fact]
    public async Task Provider_error_after_output_is_not_retried_and_incomplete_stream_is_rejected()
    {
        var provider = new RecordingProvider((_, _) => [new("partial")]) { FailAfterOutput = true };
        var budget = new RecordingBudget();
        await Assert.ThrowsAsync<ProviderRequestException>(async () => await (await Create(provider, []))
            .ExecuteAsync(Environment, budget, default));
        Assert.Equal(1, budget.Reservations);
        Assert.Single(provider.Requests);
        var incomplete = new RecordingProvider((_, _) => [new("partial", FinishReason: "stop")]);
        await Assert.ThrowsAsync<AgentProtocolException>(async () => await (await Create(incomplete, []))
            .ExecuteAsync(Environment, new RecordingBudget(), default));
        Assert.Single(incomplete.Requests);
    }

    [Fact]
    public async Task Local_invalid_group_and_minimal_oversize_do_not_charge_or_dispatch()
    {
        var provider = new RecordingProvider((_, _) => [new("done", true, "stop")]);
        var step = await Create(provider, []);
        var budget = new RecordingBudget();
        await Assert.ThrowsAsync<AgentProtocolException>(async () => await step.ExecuteAsync(new(
            [new(ModelMessageRole.Assistant, "", [new("a", "todo.list", "{}")])], [Tool]), budget, default));
        await Assert.ThrowsAsync<PromptBudgetException>(async () => await step.ExecuteAsync(new(
            [new(ModelMessageRole.User, new string('x', 9000), [])], [Tool]), budget, default));
        Assert.Equal(0, budget.Reservations);
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task Native_step_resolves_offered_alias_and_retains_unresolved_canonical_guess()
    {
        var provider = new RecordingProvider((_, request) => [
            new("", ToolCallDelta: new(0, "a", request.Tools![0].Name, "{}")),
            new("", ToolCallDelta: new(1, "b", "todo.list", "{}")),
            new("", true, "tool_calls")]);
        var response = await (await Create(provider, [])).ExecuteAsync(Environment, new RecordingBudget(), default);
        Assert.Equal("todo.list", response.ToolCalls[0].Name);
        Assert.True(response.ToolCalls[0].IsResolved);
        Assert.False(response.ToolCalls[1].IsResolved);
    }

    [Fact]
    public async Task Context_retry_removes_only_whole_old_groups_and_preserves_latest_pair()
    {
        var provider = new RecordingProvider((attempt, _) => {
            if (attempt == 1) throw new ProviderRequestException(ProviderFailureCategory.ContextLimitExceeded, "fixture");
            return [new("done", true, "stop")];
        });
        ImmutableArray<ModelMessage> messages = [
            new(ModelMessageRole.System, "system", []), new(ModelMessageRole.User, "old", []),
            new(ModelMessageRole.Assistant, "", [new("old-call", "todo.list", "{}")]),
            new(ModelMessageRole.Tool, "{}", [], "old-call"), new(ModelMessageRole.User, "current", []),
            new(ModelMessageRole.Assistant, "", [new("latest-call", "todo.list", "{}")]),
            new(ModelMessageRole.Tool, "{}", [], "latest-call")];
        var budget = new RecordingBudget();
        await (await Create(provider, [])).ExecuteAsync(new(messages, [Tool]), budget, default);
        Assert.Equal(2, budget.Reservations);
        ModelProtocol.ValidateTranscript(provider.Requests[1].ModelMessages);
        Assert.Contains(provider.Requests[1].ModelMessages, m => m.ToolCallId == "latest-call");
        Assert.True(provider.Requests[1].EffectiveMessageCount < provider.Requests[0].EffectiveMessageCount);
    }

    [Fact]
    public async Task Budget_refusal_and_connection_change_prevent_provider_dispatch()
    {
        var provider = new RecordingProvider((_, _) => [new("done", true, "stop")]);
        var settings = new Settings();
        var step = await ConversationModelTurn.CreateAgentStepAsync(new Resolver(provider), settings, new ContextResolver(),
            new RequestTokenMeter(), _ => { }, () => { }, "mash", "fixture", default);
        await Assert.ThrowsAsync<AgentBudgetExceededException>(async () => await step.ExecuteAsync(Environment, new RefusingBudget(), default));
        settings.Value = new(new("fixture", "https://fixture.test/v1", "changed"), false);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await step.ExecuteAsync(Environment, new RecordingBudget(), default));
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task Historical_canonical_name_colliding_with_current_alias_fails_without_charge()
    {
        var provider = new RecordingProvider((_, _) => [new("done", true, "stop")]);
        var budget = new RecordingBudget();
        var aliasTool = new ToolDescriptor(ModelToolNameMap.GetWireName("todo.list"), "fixture", "{}", ToolEffect.ReadOnly);
        ImmutableArray<ModelMessage> messages = [new(ModelMessageRole.User, "fixture", []),
            new(ModelMessageRole.Assistant, "", [new("a", "todo.list", "{}")]),
            new(ModelMessageRole.Tool, "{}", [], "a")];
        await Assert.ThrowsAsync<AgentProtocolException>(async () => await (await Create(provider, []))
            .ExecuteAsync(new(messages, [aliasTool]), budget, default));
        Assert.Equal(0, budget.Reservations);
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task Automatic_budget_pruning_removes_complete_group_and_keeps_latest_results()
    {
        var provider = new RecordingProvider((_, _) => [new("done", true, "stop")]);
        ImmutableArray<ModelMessage> messages = [new(ModelMessageRole.System, "system", []),
            new(ModelMessageRole.Assistant, "", [new("old-call", "todo.list", "{}")]),
            new(ModelMessageRole.Tool, new string('x', 7000), [], "old-call"),
            new(ModelMessageRole.User, "current", []),
            new(ModelMessageRole.Assistant, "", [new("latest-call", "todo.list", "{}")]),
            new(ModelMessageRole.Tool, "{}", [], "latest-call")];
        await (await Create(provider, [])).ExecuteAsync(new(messages, [Tool]), new RecordingBudget(), default);
        var request = Assert.Single(provider.Requests);
        ModelProtocol.ValidateTranscript(request.ModelMessages);
        Assert.DoesNotContain(request.ModelMessages, m => m.ToolCallId == "old-call" || m.ToolCalls.Any(c => c.CallId == "old-call"));
        Assert.Contains(request.ModelMessages, m => m.ToolCallId == "latest-call");
    }

    [Fact]
    public async Task Last_available_request_can_return_final_but_retry_budget_refusal_cannot_dispatch()
    {
        var provider = new RecordingProvider((_, _) => [new("done", true, "stop")]);
        Assert.True((await (await Create(provider, [])).ExecuteAsync(Environment, new OneRequestBudget(), default)).IsFinal);
        var failing = new RecordingProvider((_, _) => throw new ProviderRequestException(ProviderFailureCategory.Network, "fixture"));
        await Assert.ThrowsAsync<AgentBudgetExceededException>(async () => await (await Create(failing, []))
            .ExecuteAsync(Environment, new OneRequestBudget(), default));
        Assert.Single(failing.Requests);
    }

    private static Task<IAgentModelStep> Create(RecordingProvider provider, List<ConversationUpdate> updates, int contextWindow = 8192) =>
        ConversationModelTurn.CreateAgentStepAsync(new Resolver(provider), new Settings(), new ContextResolver(contextWindow),
            new RequestTokenMeter(), updates.Add, () => { }, "mash", "fixture", default);

    private sealed class RecordingBudget : IModelRequestBudget
    {
        public int Reservations { get; private set; }
        public ValueTask ReserveAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); Reservations++; return ValueTask.CompletedTask; }
    }
    private sealed class RefusingBudget : IModelRequestBudget
    {
        public ValueTask ReserveAsync(CancellationToken token) => throw new AgentBudgetExceededException("model");
    }
    private sealed class OneRequestBudget : IModelRequestBudget
    {
        private bool _reserved;
        public ValueTask ReserveAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (_reserved) throw new AgentBudgetExceededException("model");
            _reserved = true;
            return ValueTask.CompletedTask;
        }
    }
    private sealed class Settings : IDialogueSettingsStore
    {
        public DialogueSettings Value { get; set; } = new(new("fixture", "https://fixture.test/v1", "model", toolsSupported: true), true);
        public DialogueSettings Load() => Value;
        public void Save(DialogueSettings settings) => Value = settings;
    }
    private sealed class Resolver(IChatProvider provider) : IChatProviderResolver { public IChatProvider Resolve() => provider; }
    private sealed class ContextResolver(int contextWindow = 8192) : IModelContextResolver
    {
        public Task<ModelContextLimit> ResolveAsync(ModelConnectionSettings settings, CancellationToken token) =>
            Task.FromResult(new ModelContextLimit(ModelRouteKey.From(settings), contextWindow, null, ContextLimitSource.Override, "fixture"));
    }
    private sealed class RecordingProvider(Func<int, ChatRequest, ChatStreamChunk[]> script) : IChatProvider
    {
        public string ProviderId => "fixture";
        public string ModelId => "model";
        public List<ChatRequest> Requests { get; } = [];
        public Action? BeforeAttempt { get; set; }
        public bool FailAfterOutput { get; init; }
        public async IAsyncEnumerable<ChatStreamChunk> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            BeforeAttempt?.Invoke();
            Requests.Add(request);
            var chunks = script(Requests.Count, request);
            foreach (var chunk in chunks) { await Task.Yield(); yield return chunk; }
            if (FailAfterOutput) throw new ProviderRequestException(ProviderFailureCategory.Network, "fixture");
        }
        public Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<ProviderModel>>([]);
    }
}
