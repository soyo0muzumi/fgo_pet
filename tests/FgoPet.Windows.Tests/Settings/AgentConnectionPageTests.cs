using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FgoPet.App.Bootstrap;
using FgoPet.App.ViewModels;
using FgoPet.App.Views.Settings;
using FgoPet.Core.Agents;
using FgoPet.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FgoPet.Windows.Tests.Settings;

[Trait("Category", "WindowsIntegration")]
public sealed class AgentConnectionPageTests
{
    [Fact]
    public void Production_services_resolve_and_existing_page_renders_without_starting_runtime()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var services = ServiceRegistration.AddFgoPet(new ServiceCollection(), []);
                services.AddSingleton<IAppSettingsStore>(new MemorySettings());
                services.AddSingleton<IAgentRepository>(new EmptyAgents());
                using var provider = services.BuildServiceProvider();
                var viewModel = provider.GetRequiredService<AgentConnectionSettingsViewModel>();
                viewModel.PendingSources.Add(new AgentPendingSourceViewModel(new AgentPendingSource("request-1", "codex",
                    "instance-pending", "Pending Codex", "1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(10))));
                viewModel.ApprovedSources.Add(new AgentApprovedSourceViewModel(new AgentApprovedSource("codex", "instance-approved",
                    "Approved Codex", "1", false, ["project-1"], false)));
                var page = provider.GetRequiredService<AgentConnectionSettingsView>();
                Assert.Same(viewModel, page.DataContext);
                Assert.True(viewModel.IsAdministrationAvailable);
                Assert.False(viewModel.Enabled);
                Assert.Equal(AgentRelayConnectionState.Disabled, provider.GetRequiredService<IAgentRelayRuntime>().Current.State);
                page.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri("/FgoPet.App;component/Themes/ModernGray.xaml", UriKind.Relative) });
                page.Measure(new Size(620, 800));
                page.Arrange(new Rect(0, 0, 620, 800));
                page.UpdateLayout();
                var buttons = Descendants(page).OfType<Button>().Select(button => button.Content?.ToString()).ToArray();
                Assert.Contains("测试连接", buttons);
                Assert.Contains("保存连接设置", buttons);
                Assert.Contains("刷新状态", buttons);
                Assert.Contains("批准", buttons);
                Assert.Contains("保存权限", buttons);
                Assert.Contains("撤销授权", buttons);
                Assert.False(double.IsNaN(page.DesiredSize.Height));
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

    private sealed class MemorySettings : IAppSettingsStore
    {
        public string Location => "memory";
        public AppSettings Load() => AppSettings.Defaults;
        public void Save(AppSettings settings) { }
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
}
