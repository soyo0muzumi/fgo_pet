using FgoPet.Core.Backup;
using FgoPet.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace FgoPet.Infrastructure.Backup;

/// <summary>
/// Creates a standalone SQLite snapshot from the live runtime database. The
/// snapshot is produced by SQLite itself so the source WAL is included in a
/// consistent view without copying WAL/SHM sidecars.
/// </summary>
public sealed class RuntimeDatabaseSnapshotService
{
    private readonly RuntimeDatabase _database;
    private readonly Action<string>? _safeLog;

    public RuntimeDatabaseSnapshotService(RuntimeDatabase database, Action<string>? safeLog = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _safeLog = safeLog;
    }

    public Task CreateAsync(string destinationPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        cancellationToken.ThrowIfCancellationRequested();

        var fullPath = Path.GetFullPath(destinationPath);
        if (File.Exists(fullPath))
        {
            throw new IOException("The backup snapshot destination already exists.");
        }

        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var created = false;
        try
        {
            long sourceSchemaVersion;
            using (var source = _database.Open())
            {
                sourceSchemaVersion = RuntimeDatabaseMigrator.ReadVersion(source);
                cancellationToken.ThrowIfCancellationRequested();

                using var vacuum = source.CreateCommand();
                vacuum.CommandText = "VACUUM INTO $destination";
                vacuum.Parameters.AddWithValue("$destination", fullPath);
                vacuum.ExecuteNonQuery();
                created = true;
            }

            cancellationToken.ThrowIfCancellationRequested();
            ValidateSnapshot(fullPath, sourceSchemaVersion);
            cancellationToken.ThrowIfCancellationRequested();
            _safeLog?.Invoke("backup.snapshot.created");
            return Task.CompletedTask;
        }
        catch (OperationCanceledException)
        {
            Cleanup(fullPath, created);
            throw;
        }
        catch (BackupException)
        {
            Cleanup(fullPath, created);
            throw;
        }
        catch (Exception exception)
        {
            Cleanup(fullPath, created);
            throw new BackupException(
                BackupFailureCode.DatabaseInvalid,
                "The runtime database snapshot could not be created or validated.",
                exception);
        }
    }

    private static void ValidateSnapshot(string path, long expectedSchemaVersion)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
        }.ToString());
        connection.Open();

        using (var integrity = connection.CreateCommand())
        {
            integrity.CommandText = "PRAGMA integrity_check";
            var result = integrity.ExecuteScalar()?.ToString();
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new BackupException(
                    BackupFailureCode.DatabaseInvalid,
                    "The runtime database snapshot failed SQLite integrity validation.");
            }
        }

        var actualSchemaVersion = RuntimeDatabaseMigrator.ReadVersion(connection);
        if (actualSchemaVersion != expectedSchemaVersion)
        {
            throw new BackupException(
                BackupFailureCode.DatabaseVersionUnsupported,
                "The runtime database snapshot has an unexpected schema version.");
        }
    }

    private static void Cleanup(string path, bool created)
    {
        if (!created && !File.Exists(path))
        {
            return;
        }

        foreach (var candidate in new[] { path, path + "-wal", path + "-shm" })
        {
            try
            {
                if (File.Exists(candidate))
                {
                    File.Delete(candidate);
                }
            }
            catch (IOException)
            {
                // Cleanup is best effort; the original failure remains the useful result.
            }
            catch (UnauthorizedAccessException)
            {
                // Cleanup is best effort; the original failure remains the useful result.
            }
        }
    }
}
