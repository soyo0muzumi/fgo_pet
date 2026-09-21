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

    /// <summary>
    /// Detects a structurally corrupt SQLite file and moves it aside before the
    /// next migration creates a clean database. The original is retained for
    /// manual recovery; locked or otherwise inaccessible databases are not
    /// classified as corruption here.
    /// </summary>
    public bool ArchiveIfCorrupt()
    {
        if (!File.Exists(DatabasePath))
        {
            return false;
        }

        try
        {
            // Do not put the health-check connection in the shared pool. A pooled
            // handle can survive Dispose and prevent the Windows archive move.
            using var connection = new SqliteConnection(_connectionString + ";Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA integrity_check";
            var result = command.ExecuteScalar() as string;
            if (string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        catch (SqliteException error) when (IsCorruption(error))
        {
            // Continue to archive the damaged file below.
        }

        // Microsoft.Data.Sqlite may keep pooled handles alive after the health
        // check connection is disposed. Release them before the Windows file
        // move, otherwise a valid recovery can fail with sharing violation.
        SqliteConnection.ClearAllPools();
        var suffix = $".corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
        File.Move(DatabasePath, DatabasePath + suffix);
        foreach (var sidecar in new[] { "-wal", "-shm" })
        {
            var path = DatabasePath + sidecar;
            if (File.Exists(path))
            {
                File.Move(path, path + suffix);
            }
        }

        return true;
    }

    private static bool IsCorruption(SqliteException error) =>
        error.Message.Contains("malformed", StringComparison.OrdinalIgnoreCase)
        || error.Message.Contains("corrupt", StringComparison.OrdinalIgnoreCase)
        || error.Message.Contains("not a database", StringComparison.OrdinalIgnoreCase);

    private static void Execute(SqliteConnection connection, string pragma)
    {
        using var command = connection.CreateCommand();
        command.CommandText = pragma;
        command.ExecuteNonQuery();
    }
}
