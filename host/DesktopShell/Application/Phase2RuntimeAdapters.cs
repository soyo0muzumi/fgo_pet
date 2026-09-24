using FgoPet.App.Bootstrap;
using FgoPet.App.Focus;
using FgoPet.Infrastructure.Persistence;

namespace FgoPet.App.Bootstrap;

/// <summary>Adapts the SQLite migrator to the migration boundary.</summary>
public sealed class SqliteRuntimeDatabaseMigrator(RuntimeDatabase database, FgoPet.Core.Memory.IMemoryWriteLifetime? memoryWrites = null) : IRuntimeDatabaseMigrator
{
    private bool _memorySessionStarted;
    public void Migrate()
    {
        new RuntimeDatabaseMigrator(database).Migrate();
        // Startup restore precedes this boundary; later shell activations must not rotate the session again.
        if (!_memorySessionStarted)
        {
            memoryWrites?.StartSession();
            _memorySessionStarted = true;
        }
    }
}

/// <summary>Adapts the focus service to the restore boundary.</summary>
public sealed class FocusServiceRestorer(FocusSessionService service) : IFocusRestorer
{
    public void Restore() => service.Restore();
}
