using FgoPet.App.Servants;
using FgoPet.App.Settings;
using FgoPet.Core.Geometry;
using FgoPet.Core.Packs;
using FgoPet.Core.Portraits;
using FgoPet.Core.Settings;
using Xunit;

namespace FgoPet.App.Tests.Settings;

public sealed class RolePackageDetailViewModelTests
{
    [Fact]
    public async Task Load_exposes_latest_preview_metadata_active_state_and_validated_settings()
    {
        var fixture = CreateFixture(AppSettings.Defaults with
        {
            Selection = new PortraitSelection("official.mash", "combat", "1.1.0"),
        });

        await fixture.Detail.LoadAsync();

        Assert.Equal("official.mash", fixture.Detail.PackageId);
        Assert.Equal("mash_kyrielight", fixture.Detail.ServantId);
        Assert.Equal("玛修·基列莱特", fixture.Detail.DisplayName);
        Assert.Equal("1.1.0", fixture.Detail.PackageVersion);
        Assert.Equal("来源未验证", fixture.Detail.SourceBadge);
        Assert.Equal("要求 FGO Pet 1.0.0 或更高版本", fixture.Detail.CompatibilityText);
        Assert.Equal("C:\\packs\\mash\\1.1.0\\previews\\library.png", fixture.Detail.PreviewSource);
        Assert.True(fixture.Detail.IsActive);
        Assert.True(Assert.Single(fixture.Detail.Appearances, item => item.AppearanceId == "combat").IsCurrent);
        Assert.Collection(
            fixture.Detail.PackageSettings,
            setting =>
            {
                Assert.Equal("show_status", setting.Key);
                Assert.Equal(PackSettingType.Toggle, setting.Type);
                Assert.Equal("true", setting.Value);
            },
            setting =>
            {
                Assert.Equal("voice", setting.Key);
                Assert.Equal(["jp", "cn"], setting.Options);
                Assert.Equal("jp", setting.Value);
            },
            setting =>
            {
                Assert.Equal("greeting", setting.Key);
                Assert.Equal(PackSettingType.Text, setting.Type);
                Assert.Equal("早上好", setting.Value);
            });
    }

    [Fact]
    public async Task Appearance_activation_reuses_library_behavior_and_preserves_other_settings()
    {
        var fixture = CreateFixture(AppSettings.Defaults with
        {
            Theme = AppTheme.FgoLight,
            UserProfile = new UserProfile("global profile"),
        });
        await fixture.Detail.LoadAsync();
        fixture.Detail.SelectedAppearance = fixture.Detail.Appearances.Single(item => item.AppearanceId == "combat");

        await fixture.Detail.ActivateAsync();

        var activation = Assert.Single(fixture.Controller.Activations);
        Assert.Equal(new PortraitSelection("official.mash", "combat", "1.1.0"), activation);
        Assert.Equal(activation, fixture.Settings.Current.Selection);
        Assert.Equal(AppTheme.FgoLight, fixture.Settings.Current.Theme);
        Assert.Equal("global profile", fixture.Settings.Current.UserProfile!.DisplayName);
        Assert.True(fixture.Detail.IsActive);
    }

    [Fact]
    public async Task Address_is_saved_only_under_the_stable_servant_id()
    {
        var fixture = CreateFixture(AppSettings.Defaults with
        {
            UserProfile = new UserProfile("全局名字"),
            ServantPreferences = new Dictionary<string, ServantPreference>
            {
                ["another_servant"] = new(AddressMode.UserDefined, "另一个称呼"),
            },
        });
        await fixture.Detail.LoadAsync();
        fixture.Detail.UseCustomAddress = true;
        fixture.Detail.CustomAddress = "前辈";

        await fixture.Detail.SaveAddressAsync();

        Assert.Equal("前辈", fixture.Settings.Current.ServantPreferences["mash_kyrielight"].AddressText);
        Assert.Equal("另一个称呼", fixture.Settings.Current.ServantPreferences["another_servant"].AddressText);
        Assert.Equal("全局名字", fixture.Settings.Current.UserProfile!.DisplayName);
        Assert.DoesNotContain("全局名字", fixture.Settings.Current.ServantPreferences.Values.Select(value => value.AddressText));
    }

    [Fact]
    public async Task Upgrade_revalidation_preserves_valid_values_falls_back_and_shows_notice()
    {
        var fixture = CreateFixture(AppSettings.Defaults with
        {
            PackageSettings = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["mash_kyrielight"] = new Dictionary<string, string>
                {
                    ["show_status"] = "false",
                    ["voice"] = "en",
                    ["obsolete"] = "legacy",
                },
                ["another_servant"] = new Dictionary<string, string>
                {
                    ["voice"] = "cn",
                },
            },
        });

        await fixture.Detail.LoadAsync();

        Assert.Equal("false", fixture.Detail.PackageSettings.Single(item => item.Key == "show_status").Value);
        Assert.Equal("jp", fixture.Detail.PackageSettings.Single(item => item.Key == "voice").Value);
        Assert.Equal("早上好", fixture.Detail.PackageSettings.Single(item => item.Key == "greeting").Value);
        Assert.True(fixture.Detail.IsMigrationNoticeVisible);
        Assert.Contains("默认值", fixture.Detail.MigrationNotice, StringComparison.Ordinal);

        var saved = fixture.Settings.Current.PackageSettings["mash_kyrielight"];
        Assert.Equal("false", saved["show_status"]);
        Assert.Equal("jp", saved["voice"]);
        Assert.Equal("早上好", saved["greeting"]);
        Assert.DoesNotContain("obsolete", saved.Keys);
        Assert.Equal("cn", fixture.Settings.Current.PackageSettings["another_servant"]["voice"]);
    }

    [Fact]
    public async Task Saving_package_settings_updates_only_the_current_servant()
    {
        var fixture = CreateFixture(AppSettings.Defaults with
        {
            PackageSettings = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["another_servant"] = new Dictionary<string, string> { ["voice"] = "cn" },
            },
        });
        await fixture.Detail.LoadAsync();
        fixture.Detail.PackageSettings.Single(item => item.Key == "show_status").ToggleValue = false;
        fixture.Detail.PackageSettings.Single(item => item.Key == "voice").Value = "cn";
        fixture.Detail.PackageSettings.Single(item => item.Key == "greeting").Value = "晚安";

        fixture.Detail.SavePackageSettings();

        Assert.Equal("false", fixture.Settings.Current.PackageSettings["mash_kyrielight"]["show_status"]);
        Assert.Equal("cn", fixture.Settings.Current.PackageSettings["mash_kyrielight"]["voice"]);
        Assert.Equal("晚安", fixture.Settings.Current.PackageSettings["mash_kyrielight"]["greeting"]);
        Assert.Equal("cn", fixture.Settings.Current.PackageSettings["another_servant"]["voice"]);
        Assert.Equal("角色包设置已保存。", fixture.Detail.PackageSettingsStatus);
    }

    private static Fixture CreateFixture(AppSettings initial)
    {
        var repository = new FakeRepository();
        var installer = new FakeInstaller();
        var controller = new FakePortraitController();
        var settings = new FakeSettingsStore(initial);
        var library = new ServantLibraryViewModel(repository, installer, controller, settings, _ => { });
        var shell = new SettingsViewModel(SettingsSection.RolePackages);
        var route = new PackageDetailRoute("official.mash", "玛修·基列莱特");
        var detail = new RolePackageDetailViewModel(route, library, settings, shell);
        return new Fixture(detail, repository, controller, settings);
    }

    private sealed record Fixture(
        RolePackageDetailViewModel Detail,
        FakeRepository Repository,
        FakePortraitController Controller,
        FakeSettingsStore Settings);

    private sealed class FakeRepository : IArtPackageRepository
    {
        public Task<IReadOnlyList<InstalledServant>> ListServantsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<InstalledServant>>
            ([
                new InstalledServant(
                    "official.mash",
                    "mash_kyrielight",
                    "玛修·基列莱特",
                    "C:\\packs\\mash\\1.1.0\\previews\\library.png",
                    "community",
                    [
                        new ServantAppearance("casual", "1.1.0", "C:\\packs\\mash\\1.1.0\\appearances\\casual", "C:\\packs\\mash\\1.1.0\\previews\\library.png"),
                        new ServantAppearance("combat", "1.1.0", "C:\\packs\\mash\\1.1.0\\appearances\\combat", "C:\\packs\\mash\\1.1.0\\previews\\library.png"),
                    ])
                {
                    PackageVersion = "1.1.0",
                    MinAppVersion = "1.0.0",
                    Settings =
                    [
                        new PackSettingDefinition { Key = "show_status", Label = "显示状态", Type = PackSettingType.Toggle, Default = "true" },
                        new PackSettingDefinition { Key = "voice", Label = "语音", Type = PackSettingType.Choice, Default = "jp", Options = ["jp", "cn"] },
                        new PackSettingDefinition { Key = "greeting", Label = "问候", Type = PackSettingType.Text, Default = "早上好" },
                    ],
                },
            ]);

        public Task<PackCatalog> ScanAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new PackCatalog
            ([
                new InstalledPack(
                    "official.mash",
                    "1.1.0",
                    SemVersion.Parse("1.1.0"),
                    "C:\\packs\\mash\\1.1.0",
                    "mash_kyrielight",
                    "玛修·基列莱特",
                    "C:\\packs\\mash\\1.1.0\\previews\\library.png",
                    "community",
                    [new AppearanceSlot("casual", "appearances/casual/manifest.json")]),
            ]));

        public Task<AppearanceLocation?> GetAppearanceAsync(PortraitSelection selection, CancellationToken cancellationToken) =>
            Task.FromResult<AppearanceLocation?>(null);

        public Task<AppearanceLocation?> ResolveStartupSelectionAsync(PortraitSelection? requested, CancellationToken cancellationToken) =>
            Task.FromResult<AppearanceLocation?>(null);

        public Task<bool> RemoveAsync(string packageId, string packageVersion, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task MarkLastKnownGoodAsync(PortraitSelection selection, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class FakeInstaller : IPackInstaller
    {
        public Task<PackInstallResult> InstallAsync(string archivePath, CancellationToken cancellationToken) =>
            Task.FromResult(new PackInstallResult(true, new PackIdentity("official.mash", "1.1.0"), null));
    }

    private sealed class FakePortraitController : IPortraitController
    {
        public List<PortraitSelection> Activations { get; } = [];

        public Task ActivateAsync(PortraitSelection selection, CancellationToken cancellationToken)
        {
            Activations.Add(selection);
            return Task.CompletedTask;
        }

        public void SetExpression(ExpressionSemantic semantic) { }
        public void SetScale(double scale) { }
        public void ApplyDpi(Dpi2 dpi) { }
    }

    private sealed class FakeSettingsStore(AppSettings initial) : IAppSettingsStore
    {
        public string Location => "memory";
        public AppSettings Current { get; private set; } = initial;
        public AppSettings Load() => Current;
        public void Save(AppSettings settings) => Current = settings;
    }
}
