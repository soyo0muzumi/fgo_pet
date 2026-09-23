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
    public async Task Memory_empty_surface_displays_role_and_failure_states_and_offers_retry()
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
            var page = new ConversationMemoryPage(model);
            page.Resources.MergedDictionaries.Insert(0, new ResourceDictionary
            {
                Source = new Uri("/FgoPet.App;component/Themes/FgoLight.xaml", UriKind.Relative),
            });
            page.Background = (Brush)page.FindResource("WindowBackgroundBrush");
            page.Measure(new Size(720, double.PositiveInfinity));
            page.Arrange(new Rect(new Point(), page.DesiredSize));
            page.UpdateLayout();
            var status = Assert.IsType<TextBlock>(Assert.Single(page.CandidatesEmptyState.Children.Cast<UIElement>()));
            Assert.Contains("选择角色", status.Text);
            Assert.Same(model.RefreshCommand, page.RefreshButton.Command);

            model.ActiveServantId = "a";
            await model.RefreshAsync();
            StaRunner.Pump();
            Assert.Contains("记忆加载失败", status.Text);
            Assert.DoesNotContain("synthetic", status.Text);
            fail = false;
            await model.RefreshCommand.ExecuteAsync(null);
            StaRunner.Pump();
            Assert.Contains("暂无待审核", status.Text);

            var captureDirectory = Environment.GetEnvironmentVariable("FGO_PET_UI_CAPTURE_DIR");
            if (!string.IsNullOrWhiteSpace(captureDirectory))
            {
                Directory.CreateDirectory(captureDirectory);
                page.Measure(new Size(720, double.PositiveInfinity));
                page.Arrange(new Rect(new Point(), page.DesiredSize));
                page.UpdateLayout();
                var bitmap = new RenderTargetBitmap(720, (int)Math.Ceiling(page.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(page);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(captureDirectory, "memory-empty.png"));
                encoder.Save(file);
            }
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
