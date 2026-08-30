using FgoPet.App.Settings;
using FgoPet.App.ViewModels;
using FgoPet.Core.Agents;
using FgoPet.Core.Settings;
using FgoPet.Infrastructure.Agents;
using Xunit;

namespace FgoPet.App.Tests.Settings;

public sealed class AgentConnectionSettingsViewModelTests
{
    [Fact]
    public void Saves_global_and_source_switches_without_touching_allowlist()
    {
        var settings = new FakeSettingsStore();
        var repository = new FakeAgentRepository();
        repository.Connections.Add(new PersistedAgentConnection(
            "codex", "Codex", "1", true, null, 0,
            new AgentCapabilities(true, true, OpenMode.AppOnly, new[] { new AgentProjectTarget("target-1", "Project") })));
        var viewModel = new AgentConnectionSettingsViewModel(settings, repository);

        viewModel.Enabled = true;
        viewModel.Connections.Single().IsEnabled = false;
        viewModel.Save();

        Assert.True(settings.Current.AgentConnection.Enabled);
        Assert.False(settings.Current.AgentConnection.SourceEnabled["codex"]);
        Assert.Equal("target-1", settings.Current.AgentConnection.ProjectAllowlist["codex"].Single().TargetId);
    }

    private sealed class FakeSettingsStore : IAppSettingsStore
    {
        public AppSettings Current { get; set; } = AppSettings.Defaults;
        public string Location => "test";
        public AppSettings Load() => Current;
        public void Save(AppSettings settings) => Current = settings;
    }

    private sealed class FakeAgentRepository : IAgentRepository
    {
        public List<PersistedAgentConnection> Connections { get; } = new();
        public void SaveExecution(AgentExecution execution) { }
        public AgentExecution? GetExecution(string id) => null;
        public AgentExecution? GetExecution(string sourceType, string sourceInstance, string taskId) => null;
        public IReadOnlyList<AgentExecution> ListNonTerminalExecutions() => Array.Empty<AgentExecution>();
        public AgentEventApplyResult ApplyEvent(AgentEvent agentEvent) => AgentEventApplyResult.Applied;
        public void SaveConnection(PersistedAgentConnection connection, IReadOnlyList<AgentProjectTarget> allowedTargets) { }
        public IReadOnlyList<PersistedAgentConnection> ListConnections() => Connections;
    }
}
