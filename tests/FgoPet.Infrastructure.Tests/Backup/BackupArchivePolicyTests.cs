using FgoPet.Core.Backup;
using FgoPet.Infrastructure.Backup;
using Xunit;

namespace FgoPet.Infrastructure.Tests.Backup;

public sealed class BackupArchivePolicyTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-09-02T00:00:00Z");

    [Fact]
    public void Accepts_a_manifest_with_exact_required_members()
    {
        var manifest = Manifest(BackupFormat.RequiredMembers
            .Where(path => path != BackupFormat.ManifestMember)
            .Select(path => new BackupMember(path, 10, new string('a', 64)))
            .ToArray());

        BackupArchivePolicy.ValidateManifest(manifest);
    }

    [Theory]
    [InlineData("C:/escape.json", BackupFailureCode.UnsafePath)]
    [InlineData("../escape.json", BackupFailureCode.UnsafePath)]
    [InlineData("manifest.json/child", BackupFailureCode.UnsafePath)]
    public void Rejects_unsafe_member_paths(string path, BackupFailureCode code)
    {
        var error = Assert.Throws<BackupException>(() => BackupArchivePolicy.ValidateMemberPath(path));

        Assert.Equal(code, error.Code);
    }

    [Fact]
    public void Rejects_duplicate_members_before_archive_use()
    {
        var members = BackupFormat.RequiredMembers
            .Select(path => new BackupMember(path, 10, new string('a', 64)))
            .Append(new BackupMember("settings.json", 10, new string('a', 64)))
            .ToArray();

        var error = Assert.Throws<BackupException>(() => BackupArchivePolicy.ValidateManifest(Manifest(members)));

        Assert.Equal(BackupFailureCode.DuplicateMember, error.Code);
    }

    [Fact]
    public void Rejects_missing_or_unknown_members()
    {
        var missing = BackupFormat.RequiredMembers
            .Where(path => path != BackupFormat.ManifestMember)
            .Where(path => path != "packages.json")
            .Select(path => new BackupMember(path, 10, new string('a', 64)))
            .ToArray();
        var missingError = Assert.Throws<BackupException>(() => BackupArchivePolicy.ValidateManifest(Manifest(missing)));
        Assert.Equal(BackupFailureCode.MissingMember, missingError.Code);

        var unknown = BackupFormat.RequiredMembers
            .Where(path => path != BackupFormat.ManifestMember)
            .Select(path => new BackupMember(path, 10, new string('a', 64)))
            .Append(new BackupMember("extra.json", 10, new string('a', 64)))
            .ToArray();
        var unknownError = Assert.Throws<BackupException>(() => BackupArchivePolicy.ValidateManifest(Manifest(unknown)));
        Assert.Equal(BackupFailureCode.UnexpectedMember, unknownError.Code);
    }

    [Fact]
    public void Rejects_future_format_invalid_hash_uppercase_hash_and_oversized_member()
    {
        var future = Assert.Throws<BackupException>(() => BackupArchivePolicy.ValidateManifest(
            new PrivateBackupManifest(2, "0.5.0", 8, At,
            BackupFormat.PayloadMembers.Select(path => new BackupMember(path, 10, new string('a', 64))).ToArray())));
        Assert.Equal(BackupFailureCode.UnsupportedVersion, future.Code);

        var invalidHash = BackupFormat.RequiredMembers
            .Where(path => path != BackupFormat.ManifestMember)
            .Select(path => new BackupMember(path, 10, path == "settings.json" ? "not-a-hash" : new string('a', 64)))
            .ToArray();
        var hashError = Assert.Throws<BackupException>(() => BackupArchivePolicy.ValidateManifest(Manifest(invalidHash)));
        Assert.Equal(BackupFailureCode.InvalidManifest, hashError.Code);

        var uppercaseHash = BackupFormat.PayloadMembers
            .Select(path => new BackupMember(path, 10, new string('A', 64)))
            .ToArray();
        var uppercaseError = Assert.Throws<BackupException>(() => BackupArchivePolicy.ValidateManifest(Manifest(uppercaseHash)));
        Assert.Equal(BackupFailureCode.InvalidManifest, uppercaseError.Code);

        var oversized = BackupFormat.RequiredMembers
            .Where(path => path != BackupFormat.ManifestMember)
            .Select(path => new BackupMember(path, path == "runtime.sqlite" ? BackupArchivePolicy.MaxMemberBytes + 1 : 10, new string('a', 64)))
            .ToArray();
        var sizeError = Assert.Throws<BackupException>(() => BackupArchivePolicy.ValidateManifest(Manifest(oversized)));
        Assert.Equal(BackupFailureCode.MemberTooLarge, sizeError.Code);
    }

    private static PrivateBackupManifest Manifest(IReadOnlyList<BackupMember> members) =>
        new(BackupFormat.CurrentVersion, "0.5.0", 8, At, members);
}
