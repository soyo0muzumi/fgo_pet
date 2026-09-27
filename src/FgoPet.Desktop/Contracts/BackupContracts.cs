using FgoPet.Core.Portraits;
using System.Text.Json.Serialization;

namespace FgoPet.Core.Backup;

public static class BackupFormat
{
    public const int CurrentVersion = 1;
    public const string Extension = ".fgopetbackup";
    public const string ManifestMember = "manifest.json";
    public const string RuntimeDatabaseMember = "runtime.sqlite";
    public const string SettingsMember = "settings.json";
    public const string PackagesMember = "packages.json";

    public static IReadOnlyList<string> RequiredMembers { get; } =
        new[] { ManifestMember, RuntimeDatabaseMember, SettingsMember, PackagesMember };

    public static IReadOnlyList<string> PayloadMembers { get; } =
        new[] { RuntimeDatabaseMember, SettingsMember, PackagesMember };
}

public sealed record BackupMember(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("length")] long Length,
    [property: JsonPropertyName("sha256")] string Sha256);

public sealed record PrivateBackupManifest
{
    public PrivateBackupManifest(
        int formatVersion,
        string applicationVersion,
        long databaseSchemaVersion,
        DateTimeOffset createdAtUtc,
        IReadOnlyList<BackupMember> files)
    {
        FormatVersion = formatVersion;
        ApplicationVersion = applicationVersion;
        DatabaseSchemaVersion = databaseSchemaVersion;
        CreatedAtUtc = createdAtUtc;
        Files = files?.ToArray() ?? throw new ArgumentNullException(nameof(files));
    }

    [JsonPropertyName("format_version")]
    public int FormatVersion { get; }

    [JsonPropertyName("application_version")]
    public string ApplicationVersion { get; }

    [JsonPropertyName("database_schema_version")]
    public long DatabaseSchemaVersion { get; }

    [JsonPropertyName("created_at_utc")]
    public DateTimeOffset CreatedAtUtc { get; }

    [JsonPropertyName("files")]
    public IReadOnlyList<BackupMember> Files { get; }
}

public sealed record BackupPackageReferences(PortraitSelection? Selected, PortraitSelection? LastKnownGood);

public enum BackupFailureCode
{
    InvalidManifest,
    UnsupportedVersion,
    UnsafePath,
    DuplicateMember,
    MissingMember,
    UnexpectedMember,
    MemberTooLarge,
    ArchiveTooLarge,
    DatabaseInvalid,
    DatabaseVersionUnsupported,
    SettingsInvalid,
    PackageReferencesInvalid,
    SwapFailed,
    StartupCheckFailed,
}

public sealed class BackupException : InvalidOperationException
{
    public BackupException(BackupFailureCode code, string message, Exception? innerException = null)
        : base(message, innerException) => Code = code;

    public BackupFailureCode Code { get; }
}
