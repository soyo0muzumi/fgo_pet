using System.Text.Json;
using FgoPet.Agent.Tests.TestDoubles;
using FgoPet.Extensibility;
using FgoPet.Kernel.Agent;
using Xunit;

namespace FgoPet.Agent.Tests.Skills;

public sealed partial class SkillRunScenarios
{
    [Fact]
    public async Task Skill_boundary_storage_failure_stops_before_load_or_business_execution()
    {
        var skill = new SkillProvider();
        var catalog = PluginCatalog.Create([new SkillPlugin(skill)]);
        using var runtime = new PluginRuntime(catalog);
        await runtime.StartAsync(default);
        var pipeline = new ScriptedToolPipeline();
        var model = new ScriptedAgentModelStep(new(new(ModelMessageRole.Assistant, "",
            [new("old", "fixture.read", "{}"), new("load", "skill.load", "{\"name\":\"fixture.skill\"}")]), "tool_calls", true), Final());
        var inner = new InMemoryAgentRunStore();
        // Start, step, model reservation, response, tool reservation, then deferral persistence.
        var store = new FaultInjectingAgentRunStore(inner) { FailOnCommit = 6 };
        await using var coordinator = new AgentRunCoordinator(store, (_, _) => ValueTask.FromResult<IAgentModelStep>(model),
            new StepEnvironmentBuilder(_ => [pipeline.Descriptor]), pipeline, new ScriptedRunFence(),
            extensions: new(new(catalog, runtime, new(catalog, runtime))));
        var result = await coordinator.StartAsync(Request(), default);
        Assert.Equal(AgentRunStatus.Failed, result.Snapshot.Status);
        Assert.False(result.StatePersisted);
        Assert.Empty(pipeline.Requests);
        Assert.Equal(0, skill.Loads);
        Assert.Single(model.Requests);
        var checkpoint = (await inner.LoadAsync("run", default))!;
        Assert.Equal(0, checkpoint.NextCallIndex);
        Assert.Equal(AgentCallStatus.Requested, checkpoint.Calls[0].Status);
        Assert.True(checkpoint.Calls[0].Charged);
        Assert.Null(checkpoint.Calls[0].Result);
    }

    [Fact]
    public async Task Skill_boundary_does_not_allow_deferred_call_id_to_be_replayed()
    {
        var skill = new SkillProvider();
        var catalog = PluginCatalog.Create([new SkillPlugin(skill)]);
        using var runtime = new PluginRuntime(catalog);
        await runtime.StartAsync(default);
        var pipeline = new ScriptedToolPipeline();
        var model = new ScriptedAgentModelStep(new(new(ModelMessageRole.Assistant, "",
            [new("load", "skill.load", "{\"name\":\"fixture.skill\"}"), new("old", "fixture.read", "{}")]), "tool_calls", true),
            new(new(ModelMessageRole.Assistant, "", [new("old", "fixture.read", "{}")]), "tool_calls", true));
        await using var coordinator = new AgentRunCoordinator(new InMemoryAgentRunStore(), (_, _) => ValueTask.FromResult<IAgentModelStep>(model),
            new StepEnvironmentBuilder(_ => [pipeline.Descriptor]), pipeline, new ScriptedRunFence(),
            extensions: new(new(catalog, runtime, new(catalog, runtime))));
        var result = await coordinator.StartAsync(Request(), default);
        Assert.Equal(AgentRunStatus.Failed, result.Snapshot.Status);
        Assert.Equal("MODEL_DUPLICATE_CALL_ID", result.Snapshot.ErrorCode);
        Assert.Empty(pipeline.Requests);
        Assert.Equal(1, skill.Loads);
        Assert.Equal(2, model.Requests.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Skill_boundary_defers_every_sibling_until_model_reads_body_and_reconsiders(int loadIndex)
    {
        var skill = new SkillProvider();
        var catalog = PluginCatalog.Create([new SkillPlugin(skill)]);
        using var runtime = new PluginRuntime(catalog);
        await runtime.StartAsync(default);
        var pipeline = new ScriptedToolPipeline();
        var calls = new List<ModelToolCall> { new("old-a", "fixture.read", "{\"query\":\"before\"}"),
            new("old-b", "fixture.read", "{\"query\":\"also-before\"}") };
        calls.Insert(loadIndex, new("load", "skill.load", "{\"name\":\"fixture.skill\"}"));
        var model = new ScriptedAgentModelStep(new(new(ModelMessageRole.Assistant, "", [.. calls]), "tool_calls", true),
            new(new(ModelMessageRole.Assistant, "", [new("fresh", "fixture.read", "{\"query\":\"after-reading\"}")]), "tool_calls", true), Final());
        var callsBeforeSecondRequest = -1;
        model.BeforeResponseAsync = (_, _) =>
        {
            if (model.Requests.Count == 2) callsBeforeSecondRequest = pipeline.Requests.Count;
            return ValueTask.CompletedTask;
        };
        var store = new InMemoryAgentRunStore();
        await using var coordinator = new AgentRunCoordinator(store, (_, _) => ValueTask.FromResult<IAgentModelStep>(model),
            new StepEnvironmentBuilder(_ => [pipeline.Descriptor]), pipeline, new ScriptedRunFence(),
            extensions: new(new(catalog, runtime, new(catalog, runtime))));
        var result = await coordinator.StartAsync(Request(), default);

        Assert.Equal(0, callsBeforeSecondRequest);
        Assert.Equal(AgentRunStatus.Completed, result.Snapshot.Status);
        Assert.Equal(4, result.Snapshot.ToolCalls); // Requests consume budget, including deferred calls.
        Assert.Equal("fresh", Assert.Single(pipeline.Invocations).Call.CallId);
        Assert.Contains("after-reading", pipeline.Invocations[0].Call.ArgumentsJson);
        Assert.Contains(model.Requests[1].Messages, m => m.Role == ModelMessageRole.System && m.Content.Contains(SkillProvider.Body));
        Assert.All(model.Requests, request => ModelProtocol.ValidateTranscript(request.Messages));
        var observations = model.Requests[1].Messages.Where(m => m.Role == ModelMessageRole.Tool).ToArray();
        Assert.Equal(3, observations.Length);
        foreach (var observation in observations.Where(m => m.ToolCallId != "load"))
        {
            using var json = JsonDocument.Parse(observation.Content);
            Assert.False(json.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal("NotExecuted", json.RootElement.GetProperty("executionState").GetString());
            Assert.Equal("TOOL_DEFERRED_FOR_SKILL_LOAD", json.RootElement.GetProperty("errorCode").GetString());
        }
        var checkpoint = (await store.LoadAsync("run", default))!;
        var deferred = checkpoint.CompletedSteps[0].Calls.Where(c => c.Call.CallId != "load").ToArray();
        Assert.All(deferred, call => { Assert.Equal(AgentCallStatus.NotExecuted, call.Status); Assert.True(call.Charged); });
        Assert.Equal(4, store.Events("run").Count(e => e.Kind == AgentEventKind.ToolRequested));
        Assert.Equal(2, store.Events("run").Count(e => e.Kind == AgentEventKind.ToolStarted));
    }

    [Theory]
    [InlineData("{\"name\":\"missing\"}")]
    [InlineData("{\"name\":123}")]
    public async Task Skill_boundary_failed_load_does_not_release_siblings(string arguments)
    {
        var skill = new SkillProvider();
        var catalog = PluginCatalog.Create([new SkillPlugin(skill)]);
        using var runtime = new PluginRuntime(catalog);
        await runtime.StartAsync(default);
        var pipeline = new ScriptedToolPipeline();
        var model = new ScriptedAgentModelStep(new(new(ModelMessageRole.Assistant, "",
            [new("old", "fixture.read", "{}"), new("load", "skill.load", arguments),
             new("ask", "user.ask", "{\"questions\":[{\"id\":\"q\",\"question\":\"Should not ask\"}]}")]), "tool_calls", true), Final());
        var store = new InMemoryAgentRunStore();
        await using var coordinator = new AgentRunCoordinator(store, (_, _) => ValueTask.FromResult<IAgentModelStep>(model),
            new StepEnvironmentBuilder(_ => [pipeline.Descriptor]), pipeline, new ScriptedRunFence(),
            extensions: new(new(catalog, runtime, new(catalog, runtime)), Questions: new()));
        var result = await coordinator.StartAsync(Request(), default);
        Assert.Equal(AgentRunStatus.Completed, result.Snapshot.Status);
        Assert.Empty(pipeline.Requests);
        Assert.Equal(0, skill.Loads);
        Assert.Equal(3, result.Snapshot.ToolCalls);
        Assert.DoesNotContain(model.Requests[1].Messages, m => m.Content.StartsWith("Trusted skill ", StringComparison.Ordinal));
        var first = (await store.LoadAsync("run", default))!.CompletedSteps[0];
        Assert.Equal(AgentCallStatus.Failed, first.Calls[1].Status);
        Assert.All(first.Calls.Where(c => c.Call.CallId != "load"), c => Assert.Equal(AgentCallStatus.NotExecuted, c.Status));
        Assert.DoesNotContain(store.Events("run"), e => e.Kind == AgentEventKind.UserInputRequested);
    }

    [Fact]
    public async Task Skill_boundary_multiple_loads_require_separate_model_decisions()
    {
        var skill = new SkillProvider();
        var catalog = PluginCatalog.Create([new SkillPlugin(skill)]);
        using var runtime = new PluginRuntime(catalog);
        await runtime.StartAsync(default);
        var pipeline = new ScriptedToolPipeline();
        var model = new ScriptedAgentModelStep(new(new(ModelMessageRole.Assistant, "",
            [new("first", "skill.load", "{\"name\":\"fixture.skill\"}"),
             new("second-old", "skill.load", "{\"name\":\"fixture.other\"}"), new("old-read", "fixture.read", "{}")]), "tool_calls", true),
            Load("second-fresh", "fixture.other"),
            new(new(ModelMessageRole.Assistant, "", [new("fresh-read", "fixture.read", "{}")]), "tool_calls", true), Final());
        var store = new InMemoryAgentRunStore();
        await using var coordinator = new AgentRunCoordinator(store, (_, _) => ValueTask.FromResult<IAgentModelStep>(model),
            new StepEnvironmentBuilder(_ => [pipeline.Descriptor]), pipeline, new ScriptedRunFence(),
            extensions: new(new(catalog, runtime, new(catalog, runtime))));
        var result = await coordinator.StartAsync(Request(), default);
        Assert.Equal(AgentRunStatus.Completed, result.Snapshot.Status);
        Assert.Equal(2, skill.Loads);
        Assert.Equal(5, result.Snapshot.ToolCalls);
        Assert.Equal("fresh-read", Assert.Single(pipeline.Invocations).Call.CallId);
        Assert.DoesNotContain(model.Requests[1].Messages, m => m.Content.StartsWith("Trusted skill fixture:fixture.other ", StringComparison.Ordinal));
        Assert.Contains(model.Requests[2].Messages, m => m.Content.StartsWith("Trusted skill fixture:fixture.other ", StringComparison.Ordinal));
        Assert.All(model.Requests, request => ModelProtocol.ValidateTranscript(request.Messages));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Skill_boundary_budget_or_cancellation_never_executes_deferred_calls(bool cancel)
    {
        var skill = new SkillProvider();
        var catalog = PluginCatalog.Create([new SkillPlugin(skill)]);
        using var runtime = new PluginRuntime(catalog);
        await runtime.StartAsync(default);
        using var cancellation = new CancellationTokenSource();
        var pipeline = new ScriptedToolPipeline();
        var model = new ScriptedAgentModelStep(new(new(ModelMessageRole.Assistant, "",
            [new("load", "skill.load", "{\"name\":\"fixture.skill\"}"), new("old-read", "fixture.read", "{}")]), "tool_calls", true), Final());
        model.BeforeResponseAsync = (_, _) =>
        {
            if (cancel && model.Requests.Count == 2) cancellation.Cancel();
            return ValueTask.CompletedTask;
        };
        var store = new InMemoryAgentRunStore();
        await using var coordinator = new AgentRunCoordinator(store, (_, _) => ValueTask.FromResult<IAgentModelStep>(model),
            new StepEnvironmentBuilder(_ => [pipeline.Descriptor]), pipeline, new ScriptedRunFence(),
            extensions: new(new(catalog, runtime, new(catalog, runtime))));
        var result = await coordinator.StartAsync(Request() with { Budget = new(cancel ? 2 : 1, 2, 1) }, cancellation.Token);
        Assert.Equal(cancel ? AgentRunStatus.Cancelled : AgentRunStatus.BudgetExceeded, result.Snapshot.Status);
        Assert.Empty(pipeline.Requests);
        Assert.Equal(1, skill.Loads);
        Assert.Equal(2, result.Snapshot.ToolCalls);
        var checkpoint = (await store.LoadAsync("run", default))!;
        Assert.Equal(AgentCallStatus.NotExecuted, checkpoint.CompletedSteps[0].Calls[1].Status);
        Assert.True(checkpoint.CompletedSteps[0].Calls[1].Charged);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Skill_boundary_preserves_request_budget_even_when_load_is_last(bool loadLast)
    {
        var skill = new SkillProvider();
        var catalog = PluginCatalog.Create([new SkillPlugin(skill)]);
        using var runtime = new PluginRuntime(catalog);
        await runtime.StartAsync(default);
        var pipeline = new ScriptedToolPipeline();
        var calls = new ModelToolCall[] { new("load", "skill.load", "{\"name\":\"fixture.skill\"}"), new("old", "fixture.read", "{}") };
        if (loadLast) Array.Reverse(calls);
        var model = new ScriptedAgentModelStep(new(new(ModelMessageRole.Assistant, "", [.. calls]), "tool_calls", true), Final());
        await using var coordinator = new AgentRunCoordinator(new InMemoryAgentRunStore(), (_, _) => ValueTask.FromResult<IAgentModelStep>(model),
            new StepEnvironmentBuilder(_ => [pipeline.Descriptor]), pipeline, new ScriptedRunFence(),
            extensions: new(new(catalog, runtime, new(catalog, runtime))));
        var result = await coordinator.StartAsync(Request() with { Budget = new(3, 1, 1) }, default);
        Assert.Equal(AgentRunStatus.BudgetExceeded, result.Snapshot.Status);
        Assert.Equal(1, result.Snapshot.ToolCalls);
        Assert.Equal(loadLast ? 0 : 1, skill.Loads);
        Assert.Empty(pipeline.Requests);
        Assert.Single(model.Requests);
    }
}
