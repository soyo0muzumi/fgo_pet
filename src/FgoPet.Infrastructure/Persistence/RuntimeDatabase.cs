using Microsoft.Data.Sqlite;

namespace FgoPet.Infrastructure.Persistence;

/// <summary>
/// Creates short-lived connections to the versioned runtime database. One instance
/// per application owns an absolute path; connections are never shared globally.
/// </summary>
public sealed class RuntimeDatabase
{
    private readonly string _connectionString;

    public RuntimeDatabase(string databasePath)
    {
        DatabasePath = Path.GetFullPath(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
    }

    public string DatabasePath { get; }

    public SqliteConnection Open()
    {
        var directory = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        Execute(connection, "PRAGMA foreign_keys=ON");
        Execute(connection, "PRAGMA journal_mode=WAL");
        Execute(connection, "PRAGMA busy_timeout=5000");
        return connection;
    }

    private static void Execute(SqliteConnection connection, string pragma)
    {
        using var command = connection.CreateCommand();
        command.CommandText = pragma;
        command.ExecuteNonQuery();
    }
}
