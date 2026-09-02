using FgoPet.Core.Backup;
using FgoPet.Core.Portraits;
using Xunit;

namespace FgoPet.Core.Tests.Backup;

public sealed class BackupContractTests
{
    [Fact]
    public void Manifest_copies_members_for_immutable_contract()
    {
        var members = new List<BackupMember>
        {
            new(BackupFormat.RuntimeDatabaseMember, 10, new string('a', 64)),
        };
        var manifest = new PrivateBackupManifest(
            BackupFormat.CurrentVersion,
            "0.5.0",
            8,
            DateTimeOffset.Parse("2026-09-02T00:00:00Z"),
            members);

        members.Clear();

        Assert.Single(manifest.Files);
        Assert.Equal(new PortraitSelection("official.mash", "casual", "1.0.0"),
            new BackupPackageReferences(
                new PortraitSelection("official.mash", "casual", "1.0.0"),
                null).Selected);
    }

    [Fact]
    public void Backup_format_defines_the_private_archive_members()
    {
        Assert.Equal(".fgopetbackup", BackupFormat.Extension);
        Assert.Equal(
            new[] { "manifest.json", "runtime.sqlite", "settings.json", "packages.json" },
            BackupFormat.RequiredMembers);
        Assert.Equal(
            new[] { "runtime.sqlite", "settings.json", "packages.json" },
            BackupFormat.PayloadMembers);
    }
}
