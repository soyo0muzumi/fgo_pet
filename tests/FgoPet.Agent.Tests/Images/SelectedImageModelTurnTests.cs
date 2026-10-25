using System.Runtime.CompilerServices;
using System.Text.Json;
using FgoPet.App.Dialogue;
using FgoPet.Agent.Tests.TestDoubles;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Settings;
using FgoPet.Dialogue.Settings;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Providers;
using FgoPet.Kernel.Agent;
using Xunit;

namespace FgoPet.Agent.Tests.Images;

public sealed class SelectedImageModelTurnTests
{
    [Theory]
    [InlineData("openai")]
    [InlineData("deepseek")]
    [InlineData("buddy")]
    public async Task Native_step_prepares_image_before_budgeting_and_passes_pixels_to_provider(string routeName)
    {
        var settings = new Settings(routeName switch
        {
            "buddy" => new("custom-openai-compatible", "http://127.0.0.1:8326/v1", "buddy/deepseek-v4.1-flash"),
            "deepseek" => new("deepseek", "https://api.deepseek.com/v1", "deepseek-flash"),
            _ => null,
        });
        var route = ModelRouteKey.From(settings.Load().ModelConnection!);
        var store = new SelectedImageStore(_ => true);
        SelectedImageTests.Grant(store, SelectedImageTests.Scope, route, new(SelectedImageTests.Png, 1, 1));
        var provider = new Provider(store, route);
        var step = await Create(provider, settings);
        var environment = new StepEnvironment(SelectedImageTests.Request(store.Read(SelectedImageTests.Scope, route, "run", default)).ModelMessages, []);
        var measured = ((IAgentModelInputBudget)step).MeasureInputTokens(environment);
        Assert.True(measured > 8192);
        Assert.True((await step.ExecuteAsync(environment, new Budget(), default)).IsFinal);
        Assert.Equal(1, provider.Sends);
        Assert.Contains("data:image/png;base64,", provider.Wire!);
    }
    [Fact]
    public async Task Revocation_after_budget_reservation_prevents_send_and_provider_without_opt_in_cannot_consume_image()
    {
        var store = new SelectedImageStore(_ => true);
        SelectedImageTests.Grant(store, SelectedImageTests.Scope, SelectedImageTests.Route, new(SelectedImageTests.Png, 1, 1));
        var provider = new Provider(store);
        var environment = new StepEnvironment(SelectedImageTests.Request(store.Read(SelectedImageTests.Scope, SelectedImageTests.Route, "run", default)).ModelMessages, []);
        var step = await Create(provider);
        await Assert.ThrowsAsync<SelectedImageException>(async () => await step.ExecuteAsync(environment, new Budget(store.Clear), default));
        Assert.Equal(0, provider.Sends);
        var unsupported = await Create(new TextOnlyProvider());
        var failure = await Assert.ThrowsAsync<SelectedImageException>(async () => await unsupported.ExecuteAsync(environment, new Budget(), default));
        Assert.Equal("IMAGE_MODEL_UNSUPPORTED", failure.Code);
    }
    [Fact]
    public async Task Tool_result_image_reference_survives_durable_observation_and_reaches_next_model_step()
    {
        var reference = new ToolImageReference(Guid.NewGuid().ToString("N"));
        var toolResult = new ToolResult(true, JsonSerializer.SerializeToElement(new { mimeType = "image/png" })) { Image = reference };
        var model = new ScriptedAgentModelStep(
            new(new(ModelMessageRole.Assistant, "", [new("image-call", "image.read", "{}")]), "tool_calls", true),
            new(new(ModelMessageRole.Assistant, "synthetic result", []), "stop", true));
        var pipeline = new ScriptedToolPipeline(new ToolExecutionOutcome(ToolExecutionOutcomeKind.Completed, toolResult))
            { Descriptor = new("image.read", "fixture", "{}", ToolEffect.ReadOnly) };
        var store = new InMemoryAgentRunStore();
        await using var coordinator = new AgentRunCoordinator(store, (_, _) => ValueTask.FromResult<IAgentModelStep>(model),
            new ScriptedStepEnvironmentBuilder(pipeline.Descriptor), pipeline, new ScriptedRunFence());
        var result = await coordinator.StartAsync(new(new("image-run", "user", SelectedImageTests.Scope, "model", 1), new(),
            [new(ModelMessageRole.User, "inspect selected image", [])]), default);
        Assert.Equal(AgentRunStatus.Completed, result.Snapshot.Status);
        Assert.Equal(reference, Assert.Single(model.Requests[1].Messages, m => m.Role == ModelMessageRole.Tool).Image);
        var saved = await store.LoadAsync("image-run", default);
        var calls = saved!.CompletedSteps.SelectMany(step => step.Calls).Concat(saved.Calls);
        Assert.Equal(reference, Assert.Single(calls).Result!.Image);
        Assert.DoesNotContain("base64", JsonSerializer.Serialize(saved));
    }
    private static Task<IAgentModelStep> Create(IChatProvider provider, Settings? settings = null) => ConversationModelTurn.CreateAgentStepAsync(
        new Resolver(provider), settings ?? new Settings(), new Context(), new RequestTokenMeter(), _ => { }, () => { }, "role", "conversation", default);
    private sealed class Budget(Action? reserve = null) : IModelRequestBudget
    { public ValueTask ReserveAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); reserve?.Invoke(); return ValueTask.CompletedTask; } }
    private sealed class Settings(ModelConnectionSettings? connection = null) : IDialogueSettingsStore
    {
        public DialogueSettings Load() => new(connection ?? new("openai", "https://api.openai.com/v1", "gpt-4.1-mini"), true);
        public void Save(DialogueSettings settings) { }
    }
    private sealed class Context : IModelContextResolver
    {
        public Task<ModelContextLimit> ResolveAsync(ModelConnectionSettings settings, CancellationToken token) =>
            Task.FromResult(new ModelContextLimit(ModelRouteKey.From(settings), 32768, null, ContextLimitSource.Override, "fixture"));
    }
    private sealed class Resolver(IChatProvider provider) : IChatProviderResolver { public IChatProvider Resolve() => provider; }
    private sealed class Provider(SelectedImageStore images, ModelRouteKey? route = null) : IChatProvider, IChatImageInputProvider
    {
        private readonly ModelRouteKey _route = route ?? SelectedImageTests.Route;
        public string ProviderId => _route.ProviderId;
        public string ModelId => _route.ModelId;
        public int Sends;
        public string? Wire;
        public void PrepareImages(ChatRequest request, CancellationToken token) => ChatImageInputs.Prepare(request, _route, images, ImageModelCapabilities.Supports(_route), token);
        public async IAsyncEnumerable<ChatStreamChunk> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token)
        {
            ChatImageInputs.Validate(request, _route, images, token);
            Wire = ChatRequestInputEnvelope.Write(ModelId, request); Sends++;
            await Task.Yield(); yield return new("synthetic protocol result, not a visual interpretation", true, "stop");
        }
        public Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<ProviderModel>>([]);
    }
    private sealed class TextOnlyProvider : IChatProvider
    {
        public string ProviderId => "fixture"; public string ModelId => "fixture";
        public IAsyncEnumerable<ChatStreamChunk> StreamAsync(ChatRequest request, CancellationToken cancellationToken) => throw new InvalidOperationException("must not send");
        public Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
