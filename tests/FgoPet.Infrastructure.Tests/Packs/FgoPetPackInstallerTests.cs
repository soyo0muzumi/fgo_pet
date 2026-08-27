using System.IO.Compression;
using FgoPet.Core.Packs;
using FgoPet.Infrastructure.FileSystem;
using FgoPet.Infrastructure.Packs;
using Xunit;

namespace FgoPet.Infrastructure.Tests.Packs;

public sealed class FgoPetPackInstallerTests : IDisposable
{
    private readonly string _storage = Path.Combine(Path.GetTempPath(), "fgo-pet-install-" + Guid.NewGuid().ToString("N"));
    private readonly string _packages;
    private readonly FgoPetPackInstaller _installer;

    public FgoPetPackInstallerTests()
    {
        _packages = Path.Combine(_storage, "Packages");
        _installer = new FgoPetPackInstaller(
            PackArchivePolicy.Production,
            _packages,
            _storage,
            SemVersion.Parse("1.0.0"),
            new AtomicDirectoryMover());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_storage, recursive: true);
        }
        catch (IOException)
        {
            // best effort cleanup
        }
    }

    private string Upload(string name)
    {
        var directory = Path.Combine(_storage, "upload");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, name);
    }

    private PackInstallResult Install(string archivePath) =>
        _installer.InstallAsync(archivePath, CancellationToken.None).GetAwaiter().GetResult();

    private void AssertStagingEmpty()
    {
        var staging = Path.Combine(_storage, "Staging");
        Assert.Empty(Directory.Exists(staging) ? Directory.GetFileSystemEntries(staging) : Array.Empty<string>());
    }

    // ----- happy path -----

    [Fact]
    public void Install_installs_a_valid_pack_and_returns_identity()
    {
        var archive = Upload("mash.fgopetpack");
        PackArchiveBuilder.WriteFullPack(archive);

        var result = Install(archive);

        Assert.True(result.Installed);
        Assert.Null(result.Failure);
        Assert.Equal(new PackIdentity("official.mash", "1.0.0"), result.Identity);
        Assert.True(File.Exists(Path.Combine(_packages, "official.mash", "1.0.0", "package.json")));
        Assert.True(File.Exists(Path.Combine(_packages, "official.mash", "1.0.0", "previews", "library.png")));
        AssertStagingEmpty();
    }

    [Fact]
    public void Install_never_leaves_staging_behind_after_success()
    {
        var archive = Upload("mash.fgopetpack");
        PackArchiveBuilder.WriteFullPack(archive);

        Install(archive);

        AssertStagingEmpty();
    }

    [Fact]
    public void Install_writes_a_symlink_entry_as_a_regular_file()
    {
        var archive = Upload("mash.fgopetpack");
        PackArchiveBuilder.WriteFullPack(archive);
        Assert.True(Install(archive).Installed);

        var runtimeFile = Path.Combine(_packages, "official.mash", "1.0.0", "appearances", "casual", "runtime", "full_body.png");
        var attributes = File.GetAttributes(runtimeFile);
        Assert.False(attributes.HasFlag(FileAttributes.ReparsePoint));
        Assert.False(attributes.HasFlag(FileAttributes.Directory));
    }

    // ----- structure pre-checks -----

    [Fact]
    public void Install_rejects_zip_slip_entries()
    {
        var archive = Upload("slip.fgopetpack");
        PackArchiveBuilder.Raw(archive, zip =>
        {
            PackArchiveBuilder.AddText(zip, "package.json", PackArchiveBuilder.PackManifestJson());
            PackArchiveBuilder.AddContent(zip, "../evil.png", new byte[] { 1 });
        });

        var result = Install(archive);
        Assert.False(result.Installed);
        Assert.Equal(PackErrorCode.PackagePathEscapesRoot, result.Failure!.Code);
        AssertStagingEmpty();
    }

    [Fact]
    public void Install_rejects_absolute_path_entries()
    {
        var archive = Upload("absolute.fgopetpack");
        PackArchiveBuilder.Raw(archive, zip =>
        {
            PackArchiveBuilder.AddText(zip, "package.json", PackArchiveBuilder.PackManifestJson());
            PackArchiveBuilder.AddContent(zip, "/evil.png", new byte[] { 1 });
        });

        var result = Install(archive);
        Assert.False(result.Installed);
        Assert.Equal(PackErrorCode.PackagePathEscapesRoot, result.Failure!.Code);
    }

    [Fact]
    public void Install_rejects_volume_prefixed_entries()
    {
        var archive = Upload("drive.fgopetpack");
        PackArchiveBuilder.Raw(archive, zip =>
        {
            PackArchiveBuilder.AddText(zip, "package.json", PackArchiveBuilder.PackManifestJson());
            PackArchiveBuilder.AddContent(zip, "C:\\evil.png", new byte[] { 1 });
        });

        var result = Install(archive);
        Assert.False(result.Installed);
        Assert.Equal(PackErrorCode.PackagePathEscapesRoot, result.Failure!.Code);
    }

    [Fact]
    public void Install_rejects_disallowed_file_types()
    {
        var archive = Upload("code.fgopetpack");
        PackArchiveBuilder.Raw(archive, zip =>
        {
            PackArchiveBuilder.AddText(zip, "package.json", PackArchiveBuilder.PackManifestJson());
            PackArchiveBuilder.AddContent(zip, "evil.dll", new byte[] { 1 });
        });

        var result = Install(archive);
        Assert.False(result.Installed);
        Assert.Equal(PackErrorCode.PackageArchiveInvalid, result.Failure!.Code);
    }

    [Theory]
    [InlineData(".exe")]
    [InlineData(".ps1")]
    [InlineData(".xaml")]
    [InlineData(".html")]
    public void Install_rejects_forbidden_suffixes(string suffix)
    {
        var archive = Upload("forbid.fgopetpack");
        PackArchiveBuilder.Raw(archive, zip =>
        {
            PackArchiveBuilder.AddText(zip, "package.json", PackArchiveBuilder.PackManifestJson());
            PackArchiveBuilder.AddContent(zip, $"evil{suffix}", new byte[] { 1 });
        });

        var result = Install(archive);
        Assert.False(result.Installed);
        Assert.Equal(PackErrorCode.PackageArchiveInvalid, result.Failure!.Code);
    }

    [Fact]
    public void Install_rejects_entry_count_excess()
    {
        var installer = WithPolicy(PackArchivePolicy.Production with { MaxEntries = 8 });
        var archive = Upload("many.fgopetpack");
        PackArchiveBuilder.Raw(archive, zip =>
        {
            PackArchiveBuilder.AddText(zip, "package.json", PackArchiveBuilder.PackManifestJson());
            for (var index = 0; index < 20; index++)
            {
                PackArchiveBuilder.AddContent(zip, $"pieces/p{index}.png", new byte[] { 1 });
            }
        });

        var result = installer.InstallAsync(archive, CancellationToken.None).GetAwaiter().GetResult();
        Assert.False(result.Installed);
        Assert.Equal(PackErrorCode.PackageTooLarge, result.Failure!.Code);
    }

    [Fact]
    public void Install_rejects_per_entry_size_excess()
    {
        var installer = WithPolicy(PackArchivePolicy.Production with { MaxEntryBytes = 8 });
        var archive = Upload("big.fgopetpack");
        PackArchiveBuilder.Raw(archive, zip => PackArchiveBuilder.AddContent(zip, "big.png", new byte[16]));

        var result = installer.InstallAsync(archive, CancellationToken.None).GetAwaiter().GetResult();
        Assert.False(result.Installed);
        Assert.Equal(PackErrorCode.PackageTooLarge, result.Failure!.Code);
    }

    [Fact]
    public void Install_rejects_total_expanded_excess()
    {
        var installer = WithPolicy(PackArchivePolicy.Production with { MaxExpandedBytes = 12 });
        var archive = Upload("fat.fgopetpack");
        PackArchiveBuilder.Raw(archive, zip =>
        {
            PackArchiveBuilder.AddContent(zip, "a.png", new byte[8]);
            PackArchiveBuilder.AddContent(zip, "b.png", new byte[8]);
        });

        var result = installer.InstallAsync(archive, CancellationToken.None).GetAwaiter().GetResult();
        Assert.False(result.Installed);
        Assert.Equal(PackErrorCode.PackageTooLarge, result.Failure!.Code);
    }

    [Fact]
    public void Install_rejects_duplicate_normalized_paths()
    {
        var archive = Upload("dup.fgopetpack");
        PackArchiveBuilder.Raw(archive, zip =>
        {
            PackArchiveBuilder.AddText(zip, "package.json", PackArchiveBuilder.PackManifestJson());
            PackArchiveBuilder.AddContent(zip, "dir//x.png", new byte[] { 1 });
            PackArchiveBuilder.AddContent(zip, "dir/x.png", new byte[] { 1 });
        });

        var result = Install(archive);
        Assert.False(result.Installed);
        Assert.Equal(PackErrorCode.PackageArchiveInvalid, result.Failure!.Code);
    }

    [Fact]
    public void Install_rejects_a_truncated_archive()
    {
        var archive = Upload("truncated.fgopetpack");
        File.WriteAllBytes(archive, new byte[] { 0x50, 0x4B, 0x03, 0x04, 0xFF, 0xFF, 0xFF });

        var result = Install(archive);
        Assert.False(result.Installed);
        Assert.Equal(PackErrorCode.PackageArchiveInvalid, result.Failure!.Code);
    }

    [Fact]
    public void Install_rejects_a_missing_root_package_json()
    {
        var archive = Upload("nopack.fgopetpack");
        PackArchiveBuilder.Raw(archive, zip => PackArchiveBuilder.AddContent(zip, "a.png", new byte[] { 1 }));

        var result = Install(archive);
        Assert.False(result.Installed);
        Assert.Equal(PackErrorCode.ManifestMalformed, result.Failure!.Code);
    }

    [Fact]
    public void Install_rejects_a_malformed_package_manifest()
    {
        var archive = Upload("badjson.fgopetpack");
        PackArchiveBuilder.Raw(archive, zip =>
        {
            PackArchiveBuilder.AddText(zip, "package.json", "{ not valid json");
            PackArchiveBuilder.AddContent(zip, "previews/library.png", new byte[] { 1 });
        });

        var result = Install(archive);
        Assert.False(result.Installed);
        Assert.Equal(PackErrorCode.ManifestMalformed, result.Failure!.Code);
        AssertStagingEmpty();
    }

    [Fact]
    public void Install_rejects_an_unsupported_pack_schema()
    {
        var archive = Upload("schema.fgopetpack");
        PackArchiveBuilder.Raw(archive, zip =>
        {
            var manifest = PackArchiveBuilder.PackManifestJson();
            manifest = manifest.Replace("\"schema_version\": 1", "\"schema_version\": 9", StringComparison.Ordinal);
            PackArchiveBuilder.AddText(zip, "package.json", manifest);
            PackArchiveBuilder.AddContent(zip, "previews/library.png", new byte[] { 1 });
        });

        var result = Install(archive);
        Assert.False(result.Installed);
        Assert.Equal(PackErrorCode.SchemaUnsupported, result.Failure!.Code);
    }

    // ----- versioning & compatibility -----

    [Fact]
    public void Install_refuses_to_overwrite_an_existing_version()
    {
        var archive = Upload("mash.fgopetpack");
        PackArchiveBuilder.WriteFullPack(archive);
        Assert.True(Install(archive).Installed);

        var result = Install(archive);
        Assert.False(result.Installed);
        Assert.Equal(PackErrorCode.PackageArchiveInvalid, result.Failure!.Code);
    }

    [Fact]
    public void Install_rejects_an_incompatible_app_version()
    {
        var archive = Upload("future.fgopetpack");
        PackArchiveBuilder.WriteFullPack(archive, minAppVersion: "99.0.0");

        var result = Install(archive);
        Assert.False(result.Installed);
        Assert.Equal(PackErrorCode.AppVersionIncompatible, result.Failure!.Code);
    }

    // ----- transactional cleanup -----

    [Fact]
    public void Install_cleans_staging_after_validation_failure()
    {
        var archive = Upload("badhash.fgopetpack");
        PackArchiveBuilder.Raw(archive, zip =>
        {
            PackArchiveBuilder.AddText(zip, "package.json", PackArchiveBuilder.PackManifestJson());
            PackArchiveBuilder.AddContent(zip, "previews/library.png", new byte[] { 1, 2, 3 });
            var body = new byte[] { 10, 20, 30, 40 };
            var expression = new byte[] { 11, 21, 31, 41 };
            PackArchiveBuilder.AddContent(zip, "appearances/casual/runtime/full_body.png", body);
            PackArchiveBuilder.AddContent(zip, "appearances/casual/runtime/expressions/r01c01.png", expression);
            // expression hash does not match the file content
            PackArchiveBuilder.AddText(zip, "appearances/casual/manifest.json", PackFixture.V3Json([
                ("body", "full_body", "runtime/full_body.png", PackFixture.Sha256(body)),
                ("expression", "r01c01", "runtime/expressions/r01c01.png", PackFixture.Sha256(new byte[] { 0, 0, 0, 0 })),
            ]));
        });

        var result = Install(archive);

        Assert.False(result.Installed);
        Assert.Equal(PackErrorCode.AssetHashMismatch, result.Failure!.Code);
        AssertStagingEmpty();
        Assert.False(Directory.Exists(Path.Combine(_packages, "official.mash")));
    }

    [Fact]
    public void Install_cleans_staging_and_rethrows_cancellation()
    {
        var archive = Upload("cancel.fgopetpack");
        PackArchiveBuilder.WriteFullPack(archive);
        using var source = new CancellationTokenSource();
        source.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            _installer.InstallAsync(archive, source.Token).GetAwaiter().GetResult());
        AssertStagingEmpty();
    }

    private FgoPetPackInstaller WithPolicy(PackArchivePolicy policy) => new(
        policy,
        _packages,
        _storage,
        SemVersion.Parse("1.0.0"),
        new AtomicDirectoryMover());
}

public sealed class AtomicDirectoryMoverTests
{
    [Fact]
    public void Move_relocates_a_directory()
    {
        var root = Path.Combine(Path.GetTempPath(), "fgo-pet-move-" + Guid.NewGuid().ToString("N"));
        try
        {
            var source = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;
            File.WriteAllText(Path.Combine(source, "a.txt"), "x");
            var destination = Path.Combine(root, "dst");

            new AtomicDirectoryMover().Move(source, destination);

            Assert.True(File.Exists(Path.Combine(destination, "a.txt")));
            Assert.False(Directory.Exists(source));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Move_refuses_an_existing_destination()
    {
        var root = Path.Combine(Path.GetTempPath(), "fgo-pet-move-" + Guid.NewGuid().ToString("N"));
        try
        {
            var source = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;
            Directory.CreateDirectory(Path.Combine(root, "dst"));

            Assert.Throws<IOException>(() => new AtomicDirectoryMover().Move(source, Path.Combine(root, "dst")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

public sealed class PackArchivePolicyTests
{
    [Fact]
    public void Production_defaults_are_recorded()
    {
        var production = PackArchivePolicy.Production;
        Assert.Equal(1024, production.MaxEntries);
        Assert.Equal(32L * 1024 * 1024, production.MaxEntryBytes);
        Assert.Equal(512L * 1024 * 1024, production.MaxExpandedBytes);
        foreach (var extension in new[] { ".png", ".json", ".md", ".txt" })
        {
            Assert.Contains(extension, production.AllowedExtensions);
        }
        foreach (var extension in new[] { ".dll", ".exe", ".ps1", ".xaml", ".html" })
        {
            Assert.DoesNotContain(extension, production.AllowedExtensions);
        }
    }
}