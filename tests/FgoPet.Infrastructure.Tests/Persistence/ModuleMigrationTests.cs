using FgoPet.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FgoPet.Infrastructure.Tests.Persistence;

public sealed class ModuleMigrationTests
{
    [Fact]
    public void Module_migrations_are_transactional_reentrant_and_do_not_change_global_version()
    {
        using var fixture = new Fixture();
        var migrator = new RuntimeDatabaseMigrator(fixture.Database);
        migrator.Migrate();
        var scripts = new[] { new Migration(1, "CREATE TABLE fixture_records(value TEXT NOT NULL);"),
            new Migration(2, "ALTER TABLE fixture_records ADD COLUMN revision INTEGER NOT NULL DEFAULT 1;") };
        migrator.MigrateModule("fixture.module", scripts);
        migrator.MigrateModule("fixture.module", scripts);
        using var connection = fixture.Database.Open();
        Assert.Equal(RuntimeDatabaseMigrator.CurrentSchemaVersion, RuntimeDatabaseMigrator.ReadVersion(connection));
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT MAX(version) FROM module_schema_migrations WHERE module_id='fixture.module'";
        Assert.Equal(2L, command.ExecuteScalar());
        command.CommandText = "SELECT revision FROM fixture_records";
        Assert.Null(command.ExecuteScalar());
    }

    [Fact]
    public void Failed_module_script_does_not_publish_its_ddl_or_version()
    {
        using var fixture = new Fixture();
        var migrator = new RuntimeDatabaseMigrator(fixture.Database);
        migrator.Migrate();
        Assert.Throws<SqliteException>(() => migrator.MigrateModule("fixture.module",
            [new(1, "CREATE TABLE fixture_partial(value TEXT); THIS IS NOT SQL;")]));
        using var connection = fixture.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('fixture_partial','module_schema_migrations')";
        Assert.Equal(0L, command.ExecuteScalar());
        migrator.MigrateModule("fixture.module", [new(1, "CREATE TABLE fixture_partial(value TEXT);")]);
    }

    [Fact]
    public void Future_module_version_is_rejected_without_rewriting_state()
    {
        using var fixture = new Fixture();
        var migrator = new RuntimeDatabaseMigrator(fixture.Database);
        migrator.Migrate();
        migrator.MigrateModule("fixture.module", [new(1, "CREATE TABLE fixture_records(value TEXT);")]);
        using (var connection = fixture.Database.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE module_schema_migrations SET version=9 WHERE module_id='fixture.module'";
            command.ExecuteNonQuery();
        }
        Assert.Throws<RuntimeDatabaseVersionException>(() => migrator.MigrateModule("fixture.module", [new(1, "SELECT 1;")]));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "fgo-module-fixture-" + Guid.NewGuid().ToString("N"));
        public RuntimeDatabase Database { get; }
        public Fixture() { Directory.CreateDirectory(_directory); Database = new(Path.Combine(_directory, "runtime.db"), pooling: false); }
        public void Dispose()
        {
            foreach (var file in Directory.EnumerateFiles(_directory)) File.Delete(file);
            Directory.Delete(_directory);
        }
    }
}
