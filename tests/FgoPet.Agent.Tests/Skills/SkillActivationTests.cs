using FgoPet.Agent.Tests.TestDoubles;
using FgoPet.Extensibility;
using FgoPet.Kernel.Agent;
using Xunit;

namespace FgoPet.Agent.Tests.Skills;

public sealed class SkillActivationTests
{
    [Fact]
    public async Task Eighth_unique_activation_is_valid_and_ninth_is_rejected()
    {
        var store = new InMemoryAgentRunStore();
        var state = await Runtime.RunStateTests.CreateAsync(store);
        await state.StartAsync(default);
        for (var i = 0; i < 8; i++)
            await state.ActivateSkillAsync(new("fixture." + i, "fixture", "1.0", new string('A', 64)), default);
        Assert.Equal(8, state.Snapshot.LoadedSkills);
        Assert.Equal(8, state.Checkpoint.ActiveSkills.Length);
        await Assert.ThrowsAsync<AgentBudgetExceededException>(() => state.ActivateSkillAsync(
            new("fixture.ninth", "fixture", "1.0", new string('A', 64)), default).AsTask());
        Assert.Equal(8, store.Events("run").Count(e => e.Kind == AgentEventKind.SkillLoaded));
    }

    [Fact]
    public async Task Duplicate_metadata_is_not_charged_and_changed_version_is_rejected()
    {
        var state = await Runtime.RunStateTests.CreateAsync(new InMemoryAgentRunStore(), new(1, 3, 1));
        await state.StartAsync(default);
        var skill = new ActiveSkillMetadata("fixture.skill", "fixture", "1.0", new string('A', 64));
        await state.ActivateSkillAsync(skill, default);
        await state.ActivateSkillAsync(skill, default);
        Assert.Equal(1, state.Snapshot.LoadedSkills);
        await Assert.ThrowsAsync<AgentStateException>(() => state.ActivateSkillAsync(skill with { Version = "2.0" }, default).AsTask());
        await Assert.ThrowsAsync<AgentStateException>(() => state.ActivateSkillAsync(skill with { PluginId = "different" }, default).AsTask());
        Assert.Equal(skill, Assert.Single(state.Checkpoint.ActiveSkills));
    }
}
