using System.IO;
using System.Text.Json.Nodes;
using FgoPet.Core.Packs;
using FgoPet.Core.Portraits;
using FgoPet.Infrastructure.Packs;
using Xunit;

namespace FgoPet.Infrastructure.Tests.Packs;

public sealed class FileArtPackageRepositoryTests : IDisposable
{
    private readonly string _storage = Path.Combine(Path.GetTempPath(), "fgo-pet-repo-" + Guid.NewGuid().ToString("N"));
    private readonly string _packages;
    private readonly FileArtPackageRepository _repository;

    public FileArtPackageRepositoryTests()
    {
        _packages = Path.Combine(_storage, "Packages");
        _repository = new FileArtPackageRepository(_packages, new JsonPackIndexStore(_storage));
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

    private string AddInstalledPack(
        string packageId,
        string version,
        string servantId = "mash_kyrielight",
        string displayName = "玛修·基列莱特",
        string? packageJson = null)
    {
        var packDir = Path.Combine(_packages, packageId, version);
        var appearanceDir = Path.Combine(packDir, "appearances", "casual", "runtime", "expressions");
        Directory.CreateDirectory(appearanceDir);
        Directory.CreateDirectory(Path.Combine(packDir, "previews"));

        File.WriteAllText(
            Path.Combine(packDir, "package.json"),
            packageJson ?? PackArchiveBuilder.PackManifestJson(packageId, version, servantId: servantId, displayName: displayName));
        File.WriteAllBytes(Path.Combine(packDir, "previews", "library.png"), new byte[] { 1, 2, 3 });

        var body = new byte[] { 5, 6, 7, 8 };
        var expression = new byte[] { 9, 10, 11, 12 };
        File.WriteAllBytes(Path.Combine(packDir, "appearances", "casual", "runtime", "full_body.png"), body);
        File.WriteAllBytes(Path.Combine(appearanceDir, "r01c01.png"), expression);
        File.WriteAllText(
            Path.Combine(packDir, "appearances", "casual", "manifest.json"),
            PackFixture.V3Json([
                ("body", "full_body", "runtime/full_body.png", PackFixture.Sha256(body)),
                ("expression", "r01c01", "runtime/expressions/r01c01.png", PackFixture.Sha256(expression)),
            ]));
        return packDir;
    }

    private static PortraitSelection Mash(string version) => new("official.mash", "casual", version);
    private static PortraitSelection Other(string version) => new("other.pack", "casual", version);

    // ----- scanning -----

    [Fact]
    public async Task Scan_orders_versions_deterministically()
    {
        AddInstalledPack("official.mash", "1.0.0");
        AddInstalledPack("official.mash", "1.1.0");
        AddInstalledPack("official.mash", "1.0.5");

        var catalog = await _repository.ScanAsync(CancellationToken.None);

        var versions = catalog.ForPackage("official.mash").Select(pack => pack.PackageVersion).ToArray();
        Assert.Equal(new[] { "1.1.0", "1.0.5", "1.0.0" }, versions);
    }

    [Fact]
    public async Task Scan_rejects_directories_whose_identity_mismatches()
    {
        // The directory is "official.mash" but the manifest declares another package id.
        AddInstalledPack(
            "official.mash",
            "1.0.0",
            packageJson: PackArchiveBuilder.PackManifestJson("official.mash", "1.0.0")
                .Replace("\"package_id\": \"official.mash\"", "\"package_id\": \"other.id\"", StringComparison.Ordinal));

        var catalog = await _repository.ScanAsync(CancellationToken.None);

        Assert.Empty(catalog.Packs);
    }

    [Fact]
    public async Task Scan_skips_malformed_and_non_semver_directories()
    {
        var malformed = Path.Combine(_packages, "official.mash", "1.0.0");
        Directory.CreateDirectory(Path.Combine(malformed, "previews"));
        File.WriteAllText(Path.Combine(malformed, "package.json"), "{ not valid");
        Directory.CreateDirectory(Path.Combine(_packages, "official.mash", "dev"));

        var catalog = await _repository.ScanAsync(CancellationToken.None);

        Assert.Empty(catalog.Packs);
    }

    [Fact]
    public async Task Rescan_sees_additions_and_removals()
    {
        AddInstalledPack("official.mash", "1.0.0");
        Assert.Single((await _repository.ScanAsync(CancellationToken.None)).Packs);
        AddInstalledPack("official.mash", "2.0.0");
        Assert.Equal(2, (await _repository.ScanAsync(CancellationToken.None)).Packs.Count);

        Assert.True(await _repository.RemoveAsync("official.mash", "2.0.0", CancellationToken.None));
        Assert.Single((await _repository.ScanAsync(CancellationToken.None)).Packs);
    }

    // ----- removal policy -----

    [Fact]
    public async Task Removing_the_current_pack_is_blocked_while_alternatives_exist()
    {
        await _repository.MarkLastKnownGoodAsync(Mash("1.0.0"), CancellationToken.None);
        AddInstalledPack("official.mash", "1.0.0");
        AddInstalledPack("other.pack", "1.0.0");

        var removed = await _repository.RemoveAsync("official.mash", "1.0.0", CancellationToken.None);

        Assert.False(removed);
        Assert.True(Directory.Exists(Path.Combine(_packages, "official.mash", "1.0.0")));
    }

    [Fact]
    public async Task Removing_the_final_pack_enters_the_packless_state()
    {
        await _repository.MarkLastKnownGoodAsync(Mash("1.0.0"), CancellationToken.None);
        AddInstalledPack("official.mash", "1.0.0");

        var removed = await _repository.RemoveAsync("official.mash", "1.0.0", CancellationToken.None);

        Assert.True(removed);
        Assert.False(Directory.Exists(Path.Combine(_packages, "official.mash")));
        Assert.Empty((await _repository.ScanAsync(CancellationToken.None)).Packs);
    }

    // ----- recovery -----

    [Fact]
    public async Task Recovery_prefers_the_current_valid_version()
    {
        await _repository.MarkLastKnownGoodAsync(Mash("1.0.0"), CancellationToken.None);
        AddInstalledPack("official.mash", "1.0.0");
        AddInstalledPack("official.mash", "1.1.0");

        var location = await _repository.ResolveStartupSelectionAsync(null, CancellationToken.None);

        Assert.NotNull(location);
        Assert.Equal(new PackIdentity("official.mash", "1.0.0"), location!.Identity);
    }

    [Fact]
    public async Task Recovery_falls_back_to_a_prior_version_of_the_same_package()
    {
        await _repository.MarkLastKnownGoodAsync(Mash("1.0.0"), CancellationToken.None);
        AddInstalledPack("official.mash", "1.1.0"); // stale selected version is gone

        var location = await _repository.ResolveStartupSelectionAsync(null, CancellationToken.None);

        Assert.NotNull(location);
        Assert.Equal(new PackIdentity("official.mash", "1.1.0"), location!.Identity);
    }

    [Fact]
    public async Task Recovery_falls_back_to_the_last_known_good_pack()
    {
        var store = new JsonPackIndexStore(_storage);
        store.Save(new PackIndexV1(Selected: Mash("1.0.0"), LastKnownGood: Other("1.0.0")));
        var repository = new FileArtPackageRepository(_packages, store);
        AddInstalledPack("other.pack", "1.0.0");

        var location = await repository.ResolveStartupSelectionAsync(null, CancellationToken.None);

        Assert.NotNull(location);
        Assert.Equal(new PackIdentity("other.pack", "1.0.0"), location!.Identity);
    }

    [Fact]
    public async Task Recovery_returns_packless_when_nothing_valid_exists()
    {
        await _repository.MarkLastKnownGoodAsync(Mash("1.9.9"), CancellationToken.None);

        var location = await _repository.ResolveStartupSelectionAsync(null, CancellationToken.None);

        Assert.Null(location);
    }

    [Fact]
    public async Task GetAppearance_returns_the_latest_version_for_a_null_version()
    {
        AddInstalledPack("official.mash", "1.0.0");
        AddInstalledPack("official.mash", "1.1.0");
        AddInstalledPack("official.mash", "1.0.5");

        var location = await _repository.GetAppearanceAsync(new PortraitSelection("official.mash", "casual"), CancellationToken.None);

        Assert.NotNull(location);
        Assert.Equal("1.1.0", location!.Identity.PackageVersion);
        Assert.True(Directory.Exists(location.AppearanceRoot));
    }

    // ----- index -----

    [Fact]
    public async Task A_corrupt_index_is_quarantined_and_replaced_with_defaults()
    {
        var stateDir = Path.Combine(_storage, "state");
        Directory.CreateDirectory(stateDir);
        File.WriteAllText(Path.Combine(stateDir, "index.json"), "{ this is not json");

        _ = await _repository.ResolveStartupSelectionAsync(null, CancellationToken.None);

        Assert.False(File.Exists(Path.Combine(stateDir, "index.json")));
        Assert.NotEmpty(Directory.GetFiles(stateDir, "index.json.corrupt.*"));
    }

    [Fact]
    public async Task ListServants_groups_appearances_across_packages()
    {
        AddInstalledPack("official.mash", "1.0.0");
        AddInstalledPack("other.pack", "1.0.0", servantId: "altria_pendragon", displayName: "阿尔托莉雅");

        var servants = await _repository.ListServantsAsync(CancellationToken.None);

        Assert.Equal(2, servants.Count);
        var mash = Assert.Single(servants, servant => servant.ServantId == "mash_kyrielight");
        Assert.Single(mash.Appearances);
    }

    [Fact]
    public async Task ListServants_exposes_safe_preview_latest_metadata_and_validated_settings()
    {
        var packageJson = JsonNode.Parse(PackArchiveBuilder.PackManifestJson(
            "official.mash",
            "1.2.0",
            minAppVersion: "1.1.0"))!.AsObject();
        packageJson["settings"] = new JsonArray
        {
            new JsonObject
            {
                ["key"] = "voice",
                ["label"] = "语音",
                ["type"] = "choice",
                ["default"] = "jp",
                ["options"] = new JsonArray("jp", "cn"),
            },
        };
        var packRoot = AddInstalledPack(
            "official.mash",
            "1.2.0",
            packageJson: packageJson.ToJsonString());

        var servant = Assert.Single(await _repository.ListServantsAsync(CancellationToken.None));

        Assert.Equal("1.2.0", servant.PackageVersion);
        Assert.Equal("1.1.0", servant.MinAppVersion);
        Assert.Equal(Path.Combine(packRoot, "previews", "library.png"), servant.PreviewPath);
        var setting = Assert.Single(servant.Settings);
        Assert.Equal("voice", setting.Key);
        Assert.Equal(PackSettingType.Choice, setting.Type);
        Assert.Equal(["jp", "cn"], setting.Options);
    }

    [Fact]
    public async Task Scan_diagnostics_do_not_expose_the_repository_root_or_exception_details()
    {
        Directory.CreateDirectory(_storage);
        var invalidRoot = Path.Combine(_storage, "packages-is-a-file");
        File.WriteAllText(invalidRoot, "not a directory");
        var repository = new FileArtPackageRepository(invalidRoot, new JsonPackIndexStore(_storage));

        var catalog = await repository.ScanAsync(CancellationToken.None);

        Assert.Empty(catalog.Packs);
        var issue = Assert.Single(repository.LastScanIssues);
        Assert.Contains("无法枚举角色包根目录", issue, StringComparison.Ordinal);
        Assert.DoesNotContain(invalidRoot, issue, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_storage, issue, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ListServants_ignores_a_tampered_preview_path_that_cannot_be_resolved()
    {
        var packageJson = JsonNode.Parse(PackArchiveBuilder.PackManifestJson(
            "official.mash",
            "1.2.0"))!.AsObject();
        packageJson["preview_path"] = "previews/\0library.png";
        AddInstalledPack(
            "official.mash",
            "1.2.0",
            packageJson: packageJson.ToJsonString());

        var servants = await _repository.ListServantsAsync(CancellationToken.None);

        Assert.Empty(servants);
        Assert.Contains(_repository.LastScanIssues, issue => issue.Contains("预览资源无效", StringComparison.Ordinal));
    }
}
