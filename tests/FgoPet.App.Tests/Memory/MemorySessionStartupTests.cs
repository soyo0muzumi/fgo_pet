using System.IO;
using FgoPet.App.Bootstrap;
using FgoPet.Core.Memory;
using FgoPet.Infrastructure.Memory;
using FgoPet.Infrastructure.Persistence;
using Xunit;

namespace FgoPet.App.Tests.Memory;

public sealed class MemorySessionStartupTests
{
    [Fact]
    public void Memory_session_starts_after_migration_once_per_process_not_on_each_shell_activation()
    {
        var path = Path.Combine(Path.GetTempPath(), $"memory-startup-{Guid.NewGuid():N}.db");
        try
        {
            var database = new RuntimeDatabase(path, pooling: false);
            var memory = new SqliteMemoryRepository(database);
            var startup = new SqliteRuntimeDatabaseMigrator(database, memory);
            startup.Migrate();
            var first = memory.Begin(Source("one"));
            startup.Migrate();
            var sameProcess = memory.Begin(Source("two"));
            Assert.Equal(first.Generation, sameProcess.Generation);
            new SqliteRuntimeDatabaseMigrator(database, memory).Migrate();
            Assert.NotEqual(first.Generation, memory.Begin(Source("three")).Generation);
        }
        finally { foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix); }
    }
    private static MemorySource Source(string id) => new(new("mash", null), "c", id, "f", DateTimeOffset.UtcNow, MemoryEvidenceKind.UserStatement);
}
