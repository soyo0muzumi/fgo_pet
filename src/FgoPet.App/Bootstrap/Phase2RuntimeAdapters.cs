using FgoPet.App.Bootstrap;
using FgoPet.App.Focus;
using FgoPet.Infrastructure.Persistence;

namespace FgoPet.App.Bootstrap;

/// <summary>Adapts the SQLite migrator to the migration boundary.</summary>
public sealed class SqliteRuntimeDatabaseMigrator(RuntimeDatabase database) : IRuntimeDatabaseMigrator
{
    public void Migrate() => new RuntimeDatabaseMigrator(database).Migrate();
}

/// <summary>Adapts the focus service to the restore boundary.</summary>
public sealed class FocusServiceRestorer(FocusSessionService service) : IFocusRestorer
{
    public void Restore() => service.Restore();
}
