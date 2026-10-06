using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using FgoPet.Agent.Tests.TestDoubles;
using FgoPet.Extensibility;
using FgoPet.Kernel.Agent;
using Xunit;

namespace FgoPet.Agent.Tests.Skills;

public sealed class SkillRunScenarios
{
    [Fact]
    public async Task Activated_body_reaches_later_steps_and_reload_charges_only_tool_budget()
    {
        var skill = new SkillProvider();
        var plugin = new SkillPlugin(skill);
        var catalog = PluginCatalog.Create([plugin]);
        using var runtime = new PluginRuntime(catalog);
        Assert.True((await runtime.StartAsync(default)).Succeeded);
        var registry = new SkillRegistry(catalog, runtime, new(catalog, runtime));
        var model = new ScriptedAgentModelStep(Load("a", "fixture.skill"), Load("b", "fixture.skill"), Final());
        var store = new InMemoryAgentRunStore();
        var pipeline = new ScriptedToolPipeline();
        var coordinator = new AgentRunCoordinator(store, (_, _) => ValueTask.FromResult<IAgentModelStep>(model),
            new StepEnvironmentBuilder(_ => []), pipeline, new ScriptedRunFence(), extensions: new(registry));
        var result = await coordinator.StartAsync(Request(), default);
        Assert.Equal(AgentRunStatus.Completed, result.Snapshot.Status);
        Assert.Equal(1, result.Snapshot.LoadedSkills);
        Assert.Equal(2, result.Snapshot.ToolCalls);
        Assert.Equal(2, skill.Loads);
        Assert.Empty(pipeline.Requests);
        Assert.DoesNotContain(model.Requests[0].Messages, message => message.Content.Contains(SkillProvider.Body, StringComparison.Ordinal));
        Assert.Contains(model.Requests[1].Messages, message => message.Role == ModelMessageRole.System
            && message.Content.Contains(SkillProvider.Body, StringComparison.Ordinal));
        Assert.Contains(model.Requests[2].Messages, message => message.Content.Contains("already_active", StringComparison.Ordinal));
        Assert.Equal(1, store.Events("run").Count(e => e.Kind == AgentEventKind.SkillLoaded));
        Assert.Single((await store.LoadAsync("run", default))!.ActiveSkills);
    }

    [Fact]
    public async Task Loading_another_skill_at_capacity_does_not_invoke_provider()
    {
        var skill = new SkillProvider();
        var catalog = PluginCatalog.Create([new SkillPlugin(skill)]);
        using var runtime = new PluginRuntime(catalog);
        await runtime.StartAsync(default);
        var model = new ScriptedAgentModelStep(Load("a", "fixture.skill"), Load("b", "fixture.other"), Final());
        var coordinator = new AgentRunCoordinator(new InMemoryAgentRunStore(), (_, _) => ValueTask.FromResult<IAgentModelStep>(model),
            new StepEnvironmentBuilder(_ => []), new ScriptedToolPipeline(), new ScriptedRunFence(),
            extensions: new(new(catalog, runtime, new(catalog, runtime))));
        var result = await coordinator.StartAsync(Request() with { Budget = new(3, 3, 1) }, default);
        Assert.Equal(AgentRunStatus.BudgetExceeded, result.Snapshot.Status);
        Assert.Equal(1, result.Snapshot.LoadedSkills);
        Assert.Equal(2, result.Snapshot.ToolCalls);
        Assert.Equal(1, skill.Loads);
        Assert.Equal(2, model.Requests.Count);
    }

    private static AgentRunRequest Request() => new(new("run", "user", new("conversation", "role", null), "route", 1), new(),
        [new(ModelMessageRole.User, "synthetic request", [])]);
    private static ModelStepResponse Load(string call, string name) => new(new(ModelMessageRole.Assistant, "",
        [new(call, "skill.load", "{\"name\":\"" + name + "\"}")]), "tool_calls", true);
    private static ModelStepResponse Final() => new(new(ModelMessageRole.Assistant, "done", []), "stop", true);
    private sealed class SkillProvider : ISkillProvider
    {
        public const string Body = "Use synthetic observations to complete the current bounded task.";
        public int Loads { get; private set; }
        public ImmutableArray<SkillDescriptor> Catalog { get; } =
            [new("fixture.skill", "first", [], [], "1.0", Digest()), new("fixture.other", "second", [], [], "1.0", Digest())];
        public ValueTask<SkillContent?> LoadAsync(ToolScope scope, string skillId, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Loads++;
            return ValueTask.FromResult<SkillContent?>(new(Catalog.Single(skill => skill.Id == skillId), Body));
        }
        private static string Digest() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Body)));
    }
    private sealed class SkillPlugin(ISkillProvider provider) : IFgoPetPlugin
    {
        public PluginManifest Manifest { get; } = new("fixture", "1.0", 1, []);
        public PluginContributions Contributions { get; } = PluginContributions.Empty with { Skills = [provider] };
        public ValueTask StartAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask StopAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
