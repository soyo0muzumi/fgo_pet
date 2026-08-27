using System.IO;
using FgoPet.App.Servants;
using FgoPet.Core.Packs;
using FgoPet.Core.Portraits;
using FgoPet.Core.Settings;
using Xunit;

namespace FgoPet.App.Tests.Servants;

public sealed class ServantLibraryViewModelTests
{
    [Fact]
    public async Task LoadAsync_groups_servants_and_sets_source_badges()
    {
        var (vm, _, _, _, _) = CreateViewModel();

        await vm.LoadAsync();

        Assert.Equal(2, vm.Servants.Count);
        var mash = Assert.Single(vm.Servants, card => card.PackageId == "official.mash");
        Assert.Equal("来源未验证", mash.SourceBadge);
        Assert.False(mash.IsEmbedded);
        Assert.Equal(2, mash.Appearances.Count);

        var embedded = Assert.Single(vm.Servants, card => card.PackageId == "app.builtin");
        Assert.Equal("内置", embedded.SourceBadge);
        Assert.True(embedded.IsEmbedded);
    }

    [Fact]
    public async Task Loading_preselects_the_first_servant_and_appearance()
    {
        var (vm, _, _, _, _) = CreateViewModel();

        await vm.LoadAsync();

        Assert.NotNull(vm.SelectedServant);
        Assert.Equal("official.mash", vm.SelectedServant!.PackageId);
        Assert.NotNull(vm.CurrentAppearance);
        Assert.Equal("1.0.0", vm.CurrentAppearance!.PackageVersion);
    }

    [Fact]
    public async Task Activating_a_selected_appearance_saves_settings()
    {
        var (vm, _, _, controller, settings) = CreateViewModel();
        await vm.LoadAsync();
        vm.CurrentAppearance = vm.SelectedServant!.Appearances[1]; // 1.1.0

        await vm.ActivateAsync();

        Assert.Single(controller.Activations);
        Assert.Equal("1.1.0", controller.Activations[0].PackageVersion);
        Assert.Equal(new PortraitSelection("official.mash", "casual", "1.1.0"), settings.Saved.Last().Selection);
    }

    [Fact]
    public async Task A_failed_activation_preserves_the_selection_and_shows_a_diagnostic()
    {
        var (vm, _, _, controller, settings) = CreateViewModel();
        await vm.LoadAsync();
        controller.FailNext = new PackFailure(PackErrorCode.ImageDecodeFailed, "解码失败", "runtime/full_body.png");

        await vm.ActivateAsync();

        Assert.NotNull(vm.Diagnostic);
        Assert.Equal(PackErrorCode.ImageDecodeFailed, vm.Diagnostic!.Code);
        Assert.NotNull(vm.CurrentAppearance);
        Assert.Equal("1.0.0", vm.CurrentAppearance!.PackageVersion);
        Assert.Empty(settings.Saved); // settings not persisted on failure
    }

    [Fact]
    public async Task Installing_refreshes_without_auto_activation()
    {
        var (vm, _, installer, controller, _) = CreateViewModel();
        await vm.LoadAsync();

        var archive = Path.Combine(Path.GetTempPath(), "vm-packs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(archive);
        var packPath = Path.Combine(archive, "x.fgopetpack");
        File.WriteAllText(packPath, "stub");
        try
        {
            await vm.InstallAsync(packPath);
        }
        finally
        {
            Directory.Delete(archive, recursive: true);
        }

        Assert.Equal(1, installer.Calls);
        Assert.Empty(controller.Activations);
        Assert.Null(vm.Diagnostic);
    }

    [Fact]
    public void The_uninstall_command_is_disabled_for_embedded_packs()
    {
        var (vm, _, _, _, _) = CreateViewModel();
        vm.Servants = new[]
        {
            new ServantCardViewModel("app.builtin", "builtin", "内置", "内置", isEmbedded: true,
                new[] { new ServantAppearanceItemViewModel("casual", "1.0.0", null) }),
        };
        vm.SelectedServant = vm.Servants[0];
        vm.CurrentAppearance = vm.SelectedServant.Appearances[0];

        Assert.False(vm.UninstallCommand.CanExecute(null));
    }

    [Fact]
    public async Task Uninstalling_the_current_pack_while_alternatives_exist_is_blocked()
    {
        var (vm, repository, _, _, _) = CreateViewModel();
        await vm.LoadAsync();

        await vm.UninstallAsync();

        Assert.NotNull(vm.Diagnostic);
        Assert.Equal(PackErrorCode.PackageArchiveInvalid, vm.Diagnostic!.Code);
    }

    [Fact]
    public async Task OpenPackFolder_resolves_and_opens_the_selected_pack_root()
    {
        var opened = new List<string>();
        var (vm, _, _, _, _) = CreateViewModel(openFolder: path => opened.Add(path));
        await vm.LoadAsync();

        var root = await vm.OpenPackFolderAsync();

        Assert.Equal("C:\\packs\\mash\\1.0.0", root);
        Assert.Equal(root, Assert.Single(opened));
    }

    [Fact]
    public void PackageDiagnostic_redacts_absolute_paths()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "secret.bin");
        var diagnostic = new PackageDiagnosticViewModel(new PackFailure(PackErrorCode.AssetMissing, $"缺失 {absolute}"));
        Assert.Equal("AssetMissing", diagnostic.Text);
        Assert.DoesNotContain(absolute, diagnostic.Text);

        var relative = new PackageDiagnosticViewModel(new PackFailure(PackErrorCode.AssetHashMismatch, "哈希不符", "runtime/full_body.png"));
        Assert.Equal("AssetHashMismatch runtime/full_body.png", relative.Text);
    }

    private static (ServantLibraryViewModel Vm, FakeArtRepository Repository, FakeInstaller Installer, FakePortraitController Controller, FakeSettingsStore Settings) CreateViewModel(
        Action<string>? openFolder = null)
    {
        var repository = new FakeArtRepository();
        var installer = new FakeInstaller();
        var controller = new FakePortraitController();
        var settings = new FakeSettingsStore();
        var vm = new ServantLibraryViewModel(repository, installer, controller, settings, openFolder ?? (_ => { }));
        return (vm, repository, installer, controller, settings);
    }

    private sealed class FakeInstaller : IPackInstaller
    {
        public int Calls { get; private set; }

        public Task<PackInstallResult> InstallAsync(string archivePath, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new PackInstallResult(true, new PackIdentity("official.mash", "1.0.0"), null));
        }
    }

    private sealed class FakeArtRepository : IArtPackageRepository
    {
        public Task<PackCatalog> ScanAsync(CancellationToken cancellationToken) => Task.FromResult(new PackCatalog(new[]
        {
            new InstalledPack("official.mash", "1.0.0", Core.Packs.SemVersion.Parse("1.0.0"),
                "C:\\packs\\mash\\1.0.0", "mash_kyrielight", "玛修", null, "community",
                new[] { new AppearanceSlot("casual", "appearances/casual/manifest.json") }),
        }));

        public Task<IReadOnlyList<InstalledServant>> ListServantsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<InstalledServant>>(new[]
            {
                new InstalledServant("official.mash", "mash_kyrielight", "玛修", "previews/library.png", "community",
                    new[]
                    {
                        new ServantAppearance("casual", "1.0.0", "C:\\packs\\mash\\1.0.0", null),
                        new ServantAppearance("casual", "1.1.0", "C:\\packs\\mash\\1.1.0", null),
                    }),
                new InstalledServant("app.builtin", "mash_kyrielight", "内置", null, "embedded",
                    new[]
                    {
                        new ServantAppearance("casual", "1.0.0", "C:\\packs\\builtin\\1.0.0", null),
                    }),
            });

        public Task<AppearanceLocation?> GetAppearanceAsync(PortraitSelection selection, CancellationToken cancellationToken) =>
            Task.FromResult<AppearanceLocation?>(null);

        public Task<AppearanceLocation?> ResolveStartupSelectionAsync(PortraitSelection? requested, CancellationToken cancellationToken) =>
            Task.FromResult<AppearanceLocation?>(null);

        public Task<bool> RemoveAsync(string packageId, string packageVersion, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task MarkLastKnownGoodAsync(PortraitSelection selection, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class FakePortraitController : IPortraitController
    {
        public List<PortraitSelection> Activations { get; } = new();

        public PackFailure? FailNext { get; set; }

        public Task ActivateAsync(PortraitSelection selection, CancellationToken cancellationToken)
        {
            if (FailNext is not null)
            {
                throw new PackFailureException(FailNext);
            }

            Activations.Add(selection);
            return Task.CompletedTask;
        }

        public void SetExpression(ExpressionSemantic semantic)
        {
        }

        public void SetScale(double scale)
        {
        }

        public void ApplyDpi(Core.Geometry.Dpi2 dpi)
        {
        }
    }

    private sealed class FakeSettingsStore : IAppSettingsStore
    {
        public string Location => "memory";

        public List<AppSettings> Saved { get; } = new();

        public AppSettings Load() => AppSettings.Defaults;

        public void Save(AppSettings settings) => Saved.Add(settings);
    }
}