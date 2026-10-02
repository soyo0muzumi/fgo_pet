using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using FgoPet.App.Bootstrap;
using FgoPet.App.ViewModels;
using FgoPet.App.Views;
using FgoPet.Core.Agents;
using FgoPet.Infrastructure.Agents;
using FgoPet.Work.Execution.Settings;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FgoPet.Windows.Tests.Settings;

[Trait("Category", "WindowsIntegration")]
public sealed class AgentConnectionPageTests
{
    [Fact]
    public void Production_settings_owner_resolves_without_starting_runtime()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var services = ServiceRegistration.AddFgoPet(new ServiceCollection(), []);
                services.AddSingleton<IWorkExecutionSettingsStore>(new MemorySettings());
                services.AddSingleton<IAgentRepository>(new EmptyAgents());
                services.AddSingleton<IAgentTargetCatalog>(new FakeTargetCatalog(new AgentTargetCatalogResult(
                    AgentTargetCatalogStatus.Available,
                    [new AgentTargetDescriptor("project-1", "Project A", true)])));
                using var provider = services.BuildServiceProvider();
                var viewModel = provider.GetRequiredService<AgentConnectionSettingsViewModel>();
                viewModel.PendingSources.Add(new AgentPendingSourceViewModel(new AgentPendingSource("request-1", "codex",
                    "instance-pending", "Pending Codex", "1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(10))));
                viewModel.ApprovedSources.Add(new AgentApprovedSourceViewModel(new AgentApprovedSource("codex", "instance-approved",
                    "Approved Codex", "1", false, ["project-1", "unresolved-secret"], false)));
                WaitForCompletion(viewModel.RefreshTargetsAsync());
                Assert.True(viewModel.IsAdministrationAvailable);
                Assert.False(viewModel.Enabled);
                Assert.Equal(AgentRelayConnectionState.Disabled, provider.GetRequiredService<IAgentRelayRuntime>().Current.State);
                var source = Assert.Single(viewModel.ApprovedSources);
                Assert.True(source.HasUnresolvedTargets);
                Assert.DoesNotContain("unresolved-secret", viewModel.BuildDiagnosticText());
                Assert.DoesNotContain(provider.GetServices<FgoPet.UiSdk.ISettingsWebPage>(),
                    owner => owner.SettingsPageId == "AgentConnection");
            }
            catch (Exception error) { failure = error; }
            finally
            {
                var dispatcher = System.Windows.Threading.Dispatcher.FromThread(Thread.CurrentThread);
                if (dispatcher is not null && !dispatcher.HasShutdownStarted)
                {
                    dispatcher.InvokeShutdown();
                }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "Settings construction/render exceeded its deadline.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void WaitForCompletion(Task operation) => operation.GetAwaiter().GetResult();

    private sealed class MemorySettings : IWorkExecutionSettingsStore
    {
        public WorkExecutionSettings Load() => WorkExecutionSettings.Defaults;
        public void Save(WorkExecutionSettings settings) { }
    }

    private sealed class EmptyAgents : IAgentRepository
    {
        public void SaveExecution(AgentExecution execution) => throw new NotSupportedException();
        public AgentExecution? GetExecution(string id) => null;
        public AgentExecution? GetExecution(string sourceType, string sourceInstance, string taskId) => null;
        public IReadOnlyList<AgentExecution> ListNonTerminalExecutions() => [];
        public IReadOnlyList<AgentExecution> ListTerminalExecutions(DateTimeOffset endedBefore, int limit) => [];
        public bool HasEventReceipt(string sourceType, string sourceInstance, string taskId, long sequence) => false;
        public void SaveArchiveBatch(AgentArchiveBatch batch) { }
        public AgentArchiveBatch? GetArchiveBatch(string batchId) => null;
        public IReadOnlyList<AgentArchiveBatch> ListIncompleteArchiveBatches() => [];
        public void CompleteArchiveBatch(string batchId, DateTimeOffset completedAt) { }
        public AgentEventApplyResult ApplyEvent(AgentEvent agentEvent) => AgentEventApplyResult.Applied;
        public void SaveConnection(PersistedAgentConnection connection, IReadOnlyList<AgentProjectTarget> allowedTargets) => throw new NotSupportedException();
        public IReadOnlyList<PersistedAgentConnection> ListConnections() => [];
    }

    private sealed class FakeTargetCatalog : IAgentTargetCatalog
    {
        private readonly AgentTargetCatalogResult _result;

        public FakeTargetCatalog(AgentTargetCatalogResult result) => _result = result;

        public Task<AgentTargetCatalogResult> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_result);
    }
}
