using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FgoPet.App.Bootstrap;
using FgoPet.App.Memory;
using FgoPet.App.Runtime;
using FgoPet.App.Settings;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Infrastructure.Memory;
using FgoPet.Infrastructure.Settings;
using FgoPet.Platform.Settings;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FgoPet.Windows.Tests.Settings;

public sealed class MemoryContextIntegrationTests
{
    [Fact]
    public async Task Memory_owner_reports_role_and_failure_states_and_offers_retry()
    {
        await StaRunner.RunAsync(async () =>
        {
            var fail = true;
            using var model = new MemoryViewModel(new MemoryCandidateService(
                new SqliteMemoryRepository(new RuntimeDatabase(Path.Combine(Path.GetTempPath(), $"unused-{Guid.NewGuid():N}.db"))),
                TimeProvider.System),
                (_, _) => fail
                    ? Task.FromException<(IReadOnlyList<FgoPet.Core.Memory.MemoryCandidate>, IReadOnlyList<FgoPet.Core.Memory.StoredMemory>)>(new IOException("synthetic private detail"))
                    : Task.FromResult<(IReadOnlyList<FgoPet.Core.Memory.MemoryCandidate>, IReadOnlyList<FgoPet.Core.Memory.StoredMemory>)>(([], [])));
            Assert.Contains("选择角色", model.CandidatesStatusText);

            model.ActiveServantId = "a";
            await model.RefreshAsync();
            StaRunner.Pump();
            Assert.Contains("记忆加载失败", model.CandidatesStatusText);
            Assert.DoesNotContain("synthetic", model.CandidatesStatusText);
            fail = false;
            await model.RefreshCommand.ExecuteAsync(null);
            StaRunner.Pump();
            Assert.Contains("暂无待审核", model.CandidatesStatusText);

        });
    }

    [Fact]
    public async Task Settings_memory_follows_the_current_role_and_unsubscribes_with_the_host()
    {
        await StaRunner.RunAsync(async () =>
        {
            var root = Path.Combine(Path.GetTempPath(), $"fgo-memory-context-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var database = new RuntimeDatabase(Path.Combine(root, "test.db"));
            new RuntimeDatabaseMigrator(database).Migrate();
            var runtime = new AppRuntime();
            runtime.SetActiveRole(new ActiveRoleState("test", "casual", "1.0.0", "a"));
            var services = new ServiceCollection().AddFgoPet([]);
            services.AddSingleton(database);
            services.AddSingleton(runtime);
            services.AddSingleton<ISettingsDocumentStore>(new JsonSettingsDocumentStore(root));
            using var provider = services.BuildServiceProvider();
            try
            {
                var window = provider.GetRequiredService<SettingsWindow>();
                var model = provider.GetRequiredService<MemoryViewModel>();
                Assert.Equal("a", model.ActiveServantId);

                // Role activation can complete on a worker thread. WPF state must
                // still change on the UI dispatcher, using the latest role.
                await Task.Run(() => runtime.SetActiveRole(new ActiveRoleState("test", "casual", "1.0.0", "b")));
                StaRunner.Pump(Dispatcher.CurrentDispatcher);
                Assert.Equal("b", model.ActiveServantId);

                window.Close();
                provider.Dispose();
                runtime.SetActiveRole(new ActiveRoleState("test", "casual", "1.0.0", "c"));
                StaRunner.Pump(Dispatcher.CurrentDispatcher);
                Assert.Equal("b", model.ActiveServantId);
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                // This directory is unique to this synthetic test fixture.
                Directory.Delete(root, recursive: true);
            }
        });
    }
}
