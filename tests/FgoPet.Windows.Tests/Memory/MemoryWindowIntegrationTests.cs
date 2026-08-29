using System.Runtime.ExceptionServices;
using System.IO;
using System.Threading;
using System.Windows;
using FgoPet.App.Memory;
using FgoPet.Infrastructure.Memory;
using FgoPet.Infrastructure.Persistence;
using Xunit;

namespace FgoPet.Windows.Tests.Memory;

[Trait("Category", "WindowsIntegration")]
public sealed class MemoryWindowIntegrationTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"fgo-memory-window-{Guid.NewGuid():N}.db");

    [Fact]
    public void Window_exposes_review_export_and_destructive_data_controls()
    {
        StaRun(() =>
        {
            var database = new RuntimeDatabase(_path);
            new RuntimeDatabaseMigrator(database).Migrate();
            var viewModel = new MemoryViewModel(
                new MemoryCandidateService(new SqliteMemoryRepository(database), TimeProvider.System));
            var window = new MemoryWindow(viewModel);
            try
            {
                Assert.Same(viewModel, window.DataContext);
                Assert.NotNull(window.FindName("DeleteAllButton"));
                Assert.Equal("记忆与数据", window.Title);
            }
            finally
            {
                window.Close();
            }
        });
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var file = _path + suffix;
            if (File.Exists(file)) File.Delete(file);
        }
    }

    private static void StaRun(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
