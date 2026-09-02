using System.Globalization;
using FgoPet.Core.Backup;

namespace FgoPet.Infrastructure.Backup;

public static class BackupArchivePolicy
{
    public const long MaxMemberBytes = 64L * 1024 * 1024;
    public const long MaxArchiveBytes = 128L * 1024 * 1024;

    public static void ValidateMemberPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || path.Contains('\0')
            || path.Contains('\\')
            || path.Contains('/')
            || Path.IsPathRooted(path)
            || path is "." or "..")
        {
            throw new BackupException(BackupFailureCode.UnsafePath, "Backup member path is unsafe.");
        }
    }

    public static void ValidateManifest(PrivateBackupManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.FormatVersion != BackupFormat.CurrentVersion)
        {
            throw new BackupException(BackupFailureCode.UnsupportedVersion, "Backup format version is not supported.");
        }

        if (string.IsNullOrWhiteSpace(manifest.ApplicationVersion)
            || manifest.DatabaseSchemaVersion < 1
            || manifest.CreatedAtUtc == DateTimeOffset.MinValue)
        {
            throw new BackupException(BackupFailureCode.InvalidManifest, "Backup manifest metadata is invalid.");
        }

        var members = manifest.Files ?? throw new BackupException(
            BackupFailureCode.InvalidManifest, "Backup manifest member list is missing.");
        var duplicate = members
            .GroupBy(member => member.Path, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new BackupException(BackupFailureCode.DuplicateMember, "Backup manifest contains duplicate members.");
        }

        foreach (var member in members)
        {
            ValidateMemberPath(member.Path);
            if (!BackupFormat.PayloadMembers.Contains(member.Path, StringComparer.Ordinal))
            {
                throw new BackupException(BackupFailureCode.UnexpectedMember, "Backup manifest contains an unexpected member.");
            }

            if (member.Length < 0 || member.Length > MaxMemberBytes)
            {
                throw new BackupException(BackupFailureCode.MemberTooLarge, "Backup member exceeds the size limit.");
            }

            if (member.Sha256 is not { Length: 64 }
                || !member.Sha256.All(IsHexDigit)
                || !string.Equals(member.Sha256, member.Sha256.ToLowerInvariant(), StringComparison.Ordinal))
            {
                throw new BackupException(BackupFailureCode.InvalidManifest, "Backup member hash is invalid.");
            }
        }

        foreach (var required in BackupFormat.PayloadMembers)
        {
            if (!members.Any(member => string.Equals(member.Path, required, StringComparison.Ordinal)))
            {
                throw new BackupException(BackupFailureCode.MissingMember, "Backup manifest is missing a payload member.");
            }
        }

        if (members.Sum(member => member.Length) > MaxArchiveBytes)
        {
            throw new BackupException(BackupFailureCode.ArchiveTooLarge, "Backup payload exceeds the size limit.");
        }
    }

    private static bool IsHexDigit(char value) =>
        value is >= '0' and <= '9'
            or >= 'a' and <= 'f'
            or >= 'A' and <= 'F';
}
