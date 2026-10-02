using System.Text.Json;
using CommunityToolkit.Mvvm.Input;
using FgoPet.App.Servants;
using FgoPet.App.Settings;
using FgoPet.Character.Settings;
using FgoPet.Core.Geometry;
using FgoPet.Core.Packs;
using FgoPet.Core.Portraits;
using FgoPet.Core.Settings;
using FgoPet.UiSdk;
using Xunit;
using Xunit.Abstractions;

namespace FgoPet.App.Tests.Settings;

public sealed class RolePackagesWebPageTests
{
    private readonly ITestOutputHelper _output;
    private int _requestSequence;

    public RolePackagesWebPageTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Rejects_duplicate_and_unknown_payload_fields_before_scanning_packages()
    {
        var fixture = CreateFixture();
        var page = fixture.Page;

        var duplicate = await Send(page, "rolePackages.get",
            "{\"pageId\":\"RolePackages\",\"pageId\":\"RolePackages\"}");
        var unknown = await Send(page, "rolePackages.get",
            "{\"pageId\":\"RolePackages\",\"extra\":true}");
        var wrongOwner = await Send(page, "rolePackages.get",
            "{\"pageId\":\"Personalization\"}");

        Assert.Equal("SETTINGS_INVALID_INPUT", duplicate.ErrorCode);
        Assert.Equal("SETTINGS_INVALID_INPUT", unknown.ErrorCode);
        Assert.Equal("SETTINGS_INVALID_INPUT", wrongOwner.ErrorCode);
        Assert.Equal(0, fixture.Repository.ListCalls);
    }

    [Fact]
    public async Task Each_host_keeps_its_route_and_detail_drafts_independent()
    {
        var fixture = CreateFixture();
        var first = fixture.Page.CreateSession();
        var second = fixture.Page.CreateSession();

        var opened = await Send(first, "rolePackages.open",
            "{\"pageId\":\"RolePackages\",\"packageId\":\"community.sample\"}");
        var draft = await Send(first, "rolePackages.editAddress",
            "{\"pageId\":\"RolePackages\",\"packageId\":\"community.sample\",\"mode\":\"custom\",\"value\":\"前辈\"}");
        var secondRead = await Send(second, "rolePackages.get", "{\"pageId\":\"RolePackages\"}");

        Assert.True(opened.Success, opened.ErrorCode);
        Assert.True(draft.Success, draft.ErrorCode);
        Assert.Equal("detail", Snapshot(opened).GetProperty("route").GetString());
        Assert.False(Snapshot(opened).GetProperty("isBusy").GetBoolean());
        Assert.False(Snapshot(draft).GetProperty("isBusy").GetBoolean());
        Assert.Equal("catalog", Snapshot(secondRead).GetProperty("route").GetString());
        Assert.False(Snapshot(secondRead).GetProperty("isBusy").GetBoolean());

        var backed = await Send(first, "rolePackages.back", "{\"pageId\":\"RolePackages\"}");
        var reopened = await Send(first, "rolePackages.open",
            "{\"pageId\":\"RolePackages\",\"packageId\":\"community.sample\"}");

        Assert.Equal("catalog", Snapshot(backed).GetProperty("route").GetString());
        Assert.Equal("前辈", Snapshot(reopened).GetProperty("detail").GetProperty("address").GetProperty("customText").GetString());
    }

    [Fact]
    public async Task Concurrent_hosts_cannot_redirect_another_hosts_selected_appearance_activation()
    {
        var fixture = CreateFixture();
        var first = fixture.Page.CreateSession();
        var second = fixture.Page.CreateSession();

        var firstOpen = Send(first, "rolePackages.open",
            "{\"pageId\":\"RolePackages\",\"packageId\":\"community.sample\"}");
        var secondOpen = Send(second, "rolePackages.open",
            "{\"pageId\":\"RolePackages\",\"packageId\":\"app.builtin\"}");
        await Task.WhenAll(firstOpen, secondOpen);

        var activation = await Send(first, "rolePackages.activate",
            "{\"pageId\":\"RolePackages\",\"packageId\":\"community.sample\",\"appearanceId\":\"combat\",\"packageVersion\":\"1.2.0\"}");

        Assert.True(activation.Success, activation.ErrorCode);
        Assert.Equal(new PortraitSelection("community.sample", "combat", "1.2.0"), Assert.Single(fixture.Controller.Activations));
    }

    [Fact]
    public async Task Snapshot_contains_metadata_and_safe_preview_fields_without_local_pack_paths()
    {
        var fixture = CreateFixture();
        var result = await Send(fixture.Page, "rolePackages.get", "{\"pageId\":\"RolePackages\"}");

        Assert.True(result.Success, result.ErrorCode);
        using var serialized = JsonDocument.Parse(JsonSerializer.Serialize(result.Payload));
        var card = serialized.RootElement.GetProperty("catalog").GetProperty("packages")
            .EnumerateArray().Single(item => item.GetProperty("packageId").GetString() == "community.sample");
        Assert.Equal("community.sample", card.GetProperty("packageId").GetString());
        Assert.Equal("玛修·示例", card.GetProperty("displayName").GetString());
        Assert.True(card.TryGetProperty("previewDataUrl", out _));
        Assert.DoesNotContain("C:\\packs\\", serialized.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PackRoot", serialized.RootElement.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_is_host_local_and_rejects_unbounded_queries()
    {
        var fixture = CreateFixture();
        var first = fixture.Page.CreateSession();
        var second = fixture.Page.CreateSession();

        var filtered = await Send(first, "rolePackages.search",
            "{\"pageId\":\"RolePackages\",\"query\":\"玛修\"}");
        var untouched = await Send(second, "rolePackages.get", "{\"pageId\":\"RolePackages\"}");
        var tooLong = await Send(first, "rolePackages.search",
            "{\"pageId\":\"RolePackages\",\"query\":\"" + new string('x', 257) + "\"}");

        Assert.Equal("玛修", Snapshot(filtered).GetProperty("catalog").GetProperty("searchText").GetString());
        Assert.Equal(string.Empty, Snapshot(untouched).GetProperty("catalog").GetProperty("searchText").GetString());
        Assert.Equal("SETTINGS_INVALID_INPUT", tooLong.ErrorCode);
    }

    [Fact]
    public async Task Choosing_a_file_exposes_only_its_name_and_install_never_activates_it()
    {
        const string archivePath = "C:\\private\\selected.fgopetpack";
        var fixture = CreateFixture(filePicker: () => archivePath);

        var selected = await Send(fixture.Page, "rolePackages.chooseFile", "{\"pageId\":\"RolePackages\"}");
        Assert.True(selected.Success, selected.ErrorCode);
        using (var json = JsonDocument.Parse(JsonSerializer.Serialize(selected.Payload)))
        {
            Assert.Equal("selected.fgopetpack", json.RootElement.GetProperty("catalog").GetProperty("selectedArchiveName").GetString());
            Assert.DoesNotContain(archivePath, json.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
        }

        var installed = await Send(fixture.Page, "rolePackages.install", "{\"pageId\":\"RolePackages\"}");

        Assert.True(installed.Success, installed.ErrorCode);
        Assert.Equal(archivePath, fixture.Installer.LastArchivePath);
        Assert.Equal(1, fixture.Installer.Calls);
        Assert.Empty(fixture.Controller.Activations);
    }

    [Fact]
    public async Task Picker_cancellation_keeps_the_previous_filename_and_pre_cancelled_install_has_no_effect()
    {
        var picks = new Queue<string?>(["C:\\private\\first.fgopetpack", null]);
        var fixture = CreateFixture(filePicker: () => picks.Dequeue());
        var firstPick = await Send(fixture.Page, "rolePackages.chooseFile", "{\"pageId\":\"RolePackages\"}");
        var cancelledPick = await Send(fixture.Page, "rolePackages.chooseFile", "{\"pageId\":\"RolePackages\"}");
        await Send(fixture.Page, "rolePackages.open",
            "{\"pageId\":\"RolePackages\",\"packageId\":\"community.sample\"}");
        Assert.Equal("first.fgopetpack", Snapshot(firstPick).GetProperty("catalog").GetProperty("selectedArchiveName").GetString());
        Assert.Equal("first.fgopetpack", Snapshot(cancelledPick).GetProperty("catalog").GetProperty("selectedArchiveName").GetString());

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Send(fixture.Page,
            "rolePackages.install", "{\"pageId\":\"RolePackages\"}", cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Send(fixture.Page,
            "rolePackages.activate", "{\"pageId\":\"RolePackages\",\"packageId\":\"community.sample\",\"appearanceId\":\"casual\",\"packageVersion\":\"1.2.0\"}", cancellation.Token));
        Assert.Equal(0, fixture.Installer.Calls);
        Assert.Empty(fixture.Controller.Activations);
    }

    [Fact]
    public async Task Install_that_is_cancelled_after_commit_returns_the_completed_snapshot()
    {
        using var cancellation = new CancellationTokenSource();
        var fixture = CreateFixture(filePicker: () => "C:\\private\\committed.fgopetpack");
        await Send(fixture.Page, "rolePackages.chooseFile", "{\"pageId\":\"RolePackages\"}");
        fixture.Installer.AfterCommit = cancellation.Cancel;

        var result = await Send(fixture.Page, "rolePackages.install", "{\"pageId\":\"RolePackages\"}",
            cancellation.Token);

        Assert.True(result.Success, result.ErrorCode);
        Assert.Equal("catalog", Snapshot(result).GetProperty("route").GetString());
        Assert.Equal(1, fixture.Installer.Calls);
        Assert.Empty(fixture.Controller.Activations);
    }

    [Fact]
    public async Task Installer_failure_is_returned_as_a_diagnostic_snapshot()
    {
        var fixture = CreateFixture(filePicker: () => "C:\\private\\broken.fgopetpack");
        fixture.Installer.FailNext = true;
        await Send(fixture.Page, "rolePackages.chooseFile", "{\"pageId\":\"RolePackages\"}");

        var result = await Send(fixture.Page, "rolePackages.install", "{\"pageId\":\"RolePackages\"}");

        Assert.True(result.Success, result.ErrorCode);
        var diagnostic = Snapshot(result).GetProperty("diagnostic");
        Assert.Equal("PackageArchiveInvalid", diagnostic.GetProperty("text").GetString());
        Assert.Empty(fixture.Controller.Activations);
    }

    [Fact]
    public async Task Detail_edits_validate_declared_values_and_preserve_unrelated_character_settings()
    {
        var existingProfile = new UserProfile("global profile");
        var fixture = CreateFixture(CharacterSettings.Defaults with
        {
            UserProfile = existingProfile,
            ServantPreferences = new Dictionary<string, ServantPreference>
            {
                ["other_servant"] = new(AddressMode.UserDefined, "另一位御主"),
            },
            PackageSettings = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["other_servant"] = new Dictionary<string, string> { ["voice"] = "cn" },
            },
        });
        await Send(fixture.Page, "rolePackages.open",
            "{\"pageId\":\"RolePackages\",\"packageId\":\"community.sample\"}");

        var invalidAddress = await Send(fixture.Page, "rolePackages.editAddress",
            "{\"pageId\":\"RolePackages\",\"packageId\":\"community.sample\",\"mode\":\"custom\",\"value\":\"" + new string('x', 257) + "\"}");
        var invalidChoice = await Send(fixture.Page, "rolePackages.setSetting",
            "{\"pageId\":\"RolePackages\",\"packageId\":\"community.sample\",\"key\":\"voice\",\"value\":\"beep\"}");
        var unrelatedKey = await Send(fixture.Page, "rolePackages.setSetting",
            "{\"pageId\":\"RolePackages\",\"packageId\":\"community.sample\",\"key\":\"DisplayName\",\"value\":\"改名\"}");
        Assert.Equal("SETTINGS_INVALID_INPUT", invalidAddress.ErrorCode);
        Assert.Equal("SETTINGS_INVALID_INPUT", invalidChoice.ErrorCode);
        Assert.Equal("SETTINGS_INVALID_INPUT", unrelatedKey.ErrorCode);

        var addressDraft = await Send(fixture.Page, "rolePackages.editAddress",
            "{\"pageId\":\"RolePackages\",\"packageId\":\"community.sample\",\"mode\":\"custom\",\"value\":\"前辈\"}");
        var settingDraft = await Send(fixture.Page, "rolePackages.setSetting",
            "{\"pageId\":\"RolePackages\",\"packageId\":\"community.sample\",\"key\":\"voice\",\"value\":\"cn\"}");
        var addressSaved = await Send(fixture.Page, "rolePackages.saveAddress",
            "{\"pageId\":\"RolePackages\",\"packageId\":\"community.sample\"}");
        var settingsSaved = await Send(fixture.Page, "rolePackages.saveSettings",
            "{\"pageId\":\"RolePackages\",\"packageId\":\"community.sample\"}");

        Assert.True(addressDraft.Success, addressDraft.ErrorCode);
        Assert.True(settingDraft.Success, settingDraft.ErrorCode);
        Assert.True(addressSaved.Success, addressSaved.ErrorCode);
        Assert.True(settingsSaved.Success, settingsSaved.ErrorCode);
        Assert.Equal("前辈", fixture.Settings.Value.ServantPreferences["mash_kyrielight"].AddressText);
        Assert.Equal("cn", fixture.Settings.Value.PackageSettings["mash_kyrielight"]["voice"]);
        Assert.Equal(existingProfile, fixture.Settings.Value.UserProfile);
        Assert.Equal("另一位御主", fixture.Settings.Value.ServantPreferences["other_servant"].AddressText);
        Assert.Equal("cn", fixture.Settings.Value.PackageSettings["other_servant"]["voice"]);
    }

    [Fact]
    public async Task Detail_actions_reject_a_different_package_and_embedded_uninstall_never_reaches_repository()
    {
        var fixture = CreateFixture();
        await Send(fixture.Page, "rolePackages.open",
            "{\"pageId\":\"RolePackages\",\"packageId\":\"community.sample\"}");

        var redirected = await Send(fixture.Page, "rolePackages.activate",
            "{\"pageId\":\"RolePackages\",\"packageId\":\"app.builtin\",\"appearanceId\":\"casual\",\"packageVersion\":\"1.0.0\"}");
        var wrongAppearance = await Send(fixture.Page, "rolePackages.activate",
            "{\"pageId\":\"RolePackages\",\"packageId\":\"community.sample\",\"appearanceId\":\"removed\",\"packageVersion\":\"1.2.0\"}");
        var staleVersion = await Send(fixture.Page, "rolePackages.activate",
            "{\"pageId\":\"RolePackages\",\"packageId\":\"community.sample\",\"appearanceId\":\"casual\",\"packageVersion\":\"1.1.0\"}");
        Assert.Equal("SETTINGS_INVALID_INPUT", redirected.ErrorCode);
        Assert.Equal("SETTINGS_INVALID_INPUT", wrongAppearance.ErrorCode);
        Assert.Equal("SETTINGS_INVALID_INPUT", staleVersion.ErrorCode);
        Assert.Empty(fixture.Controller.Activations);

        await Send(fixture.Page, "rolePackages.back", "{\"pageId\":\"RolePackages\"}");
        await Send(fixture.Page, "rolePackages.open",
            "{\"pageId\":\"RolePackages\",\"packageId\":\"app.builtin\"}");
        var uninstall = await Send(fixture.Page, "rolePackages.uninstall",
            "{\"pageId\":\"RolePackages\",\"packageId\":\"app.builtin\",\"appearanceId\":\"casual\",\"packageVersion\":\"1.0.0\"}");

        Assert.False(uninstall.Success);
        Assert.Equal("SETTINGS_INVALID_INPUT", uninstall.ErrorCode);
        Assert.Equal(0, fixture.Repository.RemoveCalls);
    }

    [Fact]
    public async Task Cancelled_detail_load_does_not_persist_automatic_setting_migration()
    {
        var fixture = CreateFixture(CharacterSettings.Defaults with
        {
            PackageSettings = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["mash_kyrielight"] = new Dictionary<string, string> { ["removed-setting"] = "legacy" },
            },
        });
        fixture.Repository.ListGate = new TaskCompletionSource<IReadOnlyList<InstalledServant>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var detail = new RolePackageDetailViewModel(
            new PackageDetailRoute("community.sample", "玛修·示例"),
            fixture.Library,
            fixture.Settings,
            new SessionNavigator());
        using var cancellation = new CancellationTokenSource();

        var loading = detail.LoadAsync(cancellation.Token);
        await fixture.Repository.ListStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        fixture.Repository.ListGate.SetResult(fixture.Repository.Servants);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await loading);
        Assert.Equal(0, fixture.Settings.SaveCount);
        Assert.Equal("legacy", fixture.Settings.Value.PackageSettings["mash_kyrielight"]["removed-setting"]);
    }

    private static Fixture CreateFixture(CharacterSettings? initial = null, Func<string?>? filePicker = null)
    {
        var repository = new FakeRepository();
        var installer = new FakeInstaller();
        var controller = new FakePortraitController();
        var settings = new CharacterStore(initial ?? CharacterSettings.Defaults);
        var library = new ServantLibraryViewModel(repository, installer, controller, settings, _ => { });
        var page = new RolePackagesWebPage(settings, library, filePicker ?? (static () => null));
        return new Fixture(page, library, repository, installer, controller, settings);
    }

    private async Task<WebSurfaceCommandResult> Send(ISettingsWebPage page, string command,
        string json, CancellationToken cancellationToken = default)
    {
        using var document = JsonDocument.Parse(json);
        var requestId = $"role-packages-{Interlocked.Increment(ref _requestSequence)}";
        LogBoundary("owner-dispatch", requestId, command, "started");
        try
        {
            var result = await page.HandleCommandAsync(new WebSurfaceMessage(command, requestId,
                document.RootElement.Clone()), cancellationToken);
            LogBoundary("owner-result", requestId, command,
                result.Success ? "success" : "error", result.ErrorCode);
            return result;
        }
        catch (OperationCanceledException)
        {
            LogBoundary("owner-result", requestId, command, "cancelled", "OPERATION_CANCELLED");
            throw;
        }
        catch (Exception)
        {
            LogBoundary("owner-result", requestId, command, "error", "UNEXPECTED");
            throw;
        }
    }

    private void LogBoundary(string phase, string requestId, string command, string outcome,
        string? errorCode = null) => _output.WriteLine(JsonSerializer.Serialize(new
        {
            time = DateTimeOffset.UtcNow.ToString("O"),
            phase,
            requestId,
            command,
            outcome,
            errorCode,
        }));

    private static JsonElement Snapshot(WebSurfaceCommandResult result) =>
        JsonDocument.Parse(JsonSerializer.Serialize(result.Payload)).RootElement.Clone();

    private sealed record Fixture(
        RolePackagesWebPage Page,
        ServantLibraryViewModel Library,
        FakeRepository Repository,
        FakeInstaller Installer,
        FakePortraitController Controller,
        CharacterStore Settings);

    private sealed class FakeRepository : IArtPackageRepository
    {
        public IReadOnlyList<InstalledServant> Servants { get; } =
        [
            new InstalledServant("community.sample", "mash_kyrielight", "玛修·示例", "C:\\packs\\sample\\preview.png", "community",
            [
                new ServantAppearance("casual", "1.2.0", "C:\\packs\\sample\\1.2.0", "C:\\packs\\sample\\preview.png"),
                new ServantAppearance("combat", "1.2.0", "C:\\packs\\sample\\1.2.0", "C:\\packs\\sample\\preview.png"),
            ])
            {
                PackageVersion = "1.2.0",
                MinAppVersion = "1.0.0",
                Settings =
                [
                    new PackSettingDefinition { Key = "show_status", Label = "显示状态", Type = PackSettingType.Toggle, Default = "true" },
                    new PackSettingDefinition { Key = "voice", Label = "语音", Type = PackSettingType.Choice, Default = "jp", Options = ["jp", "cn"] },
                    new PackSettingDefinition { Key = "greeting", Label = "问候", Type = PackSettingType.Text, Default = "早上好" },
                ],
                DefaultAddress = "御主",
                Capabilities = ["art.v3", "persona.v1"],
            },
            new InstalledServant("app.builtin", "builtin_servant", "内置角色", null, "embedded",
            [new ServantAppearance("casual", "1.0.0", "C:\\packs\\builtin\\1.0.0", null)])
            {
                PackageVersion = "1.0.0",
                Settings = [],
            },
        ];

        public int ListCalls { get; private set; }
        public int RemoveCalls { get; private set; }
        public TaskCompletionSource<IReadOnlyList<InstalledServant>>? ListGate { get; set; }
        public TaskCompletionSource ListStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IReadOnlyList<InstalledServant>> ListServantsAsync(CancellationToken cancellationToken)
        {
            ListCalls++;
            ListStarted.TrySetResult();
            if (ListGate is not null)
                return await ListGate.Task;
            return Servants;
        }

        public Task<PackCatalog> ScanAsync(CancellationToken cancellationToken) => Task.FromResult(new PackCatalog(
        [
            new InstalledPack("community.sample", "1.2.0", SemVersion.Parse("1.2.0"), "C:\\packs\\sample\\1.2.0", "mash_kyrielight", "玛修·示例", "C:\\packs\\sample\\preview.png", "community",
                [new AppearanceSlot("casual", "appearances/casual/manifest.json"), new AppearanceSlot("combat", "appearances/combat/manifest.json")]),
            new InstalledPack("app.builtin", "1.0.0", SemVersion.Parse("1.0.0"), "C:\\packs\\builtin\\1.0.0", "builtin_servant", "内置角色", null, "embedded",
                [new AppearanceSlot("casual", "appearances/casual/manifest.json")]),
        ]));

        public Task<AppearanceLocation?> GetAppearanceAsync(PortraitSelection selection, CancellationToken cancellationToken) =>
            Task.FromResult<AppearanceLocation?>(null);

        public Task<AppearanceLocation?> ResolveStartupSelectionAsync(PortraitSelection? requested, CancellationToken cancellationToken) =>
            Task.FromResult<AppearanceLocation?>(null);

        public Task<bool> RemoveAsync(string packageId, string packageVersion, CancellationToken cancellationToken)
        {
            RemoveCalls++;
            return Task.FromResult(true);
        }

        public Task MarkLastKnownGoodAsync(PortraitSelection selection, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeInstaller : IPackInstaller
    {
        public int Calls { get; private set; }
        public string? LastArchivePath { get; private set; }
        public bool FailNext { get; set; }
        public Action? AfterCommit { get; set; }

        public Task<PackInstallResult> InstallAsync(string archivePath, CancellationToken cancellationToken)
        {
            Calls++;
            LastArchivePath = archivePath;
            if (FailNext)
            {
                FailNext = false;
                return Task.FromResult(new PackInstallResult(false, null,
                    new PackFailure(PackErrorCode.PackageArchiveInvalid, "synthetic installer failure")));
            }
            AfterCommit?.Invoke();
            return Task.FromResult(new PackInstallResult(true, new PackIdentity("community.installed", "1.0.0"), null));
        }
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

    private sealed class CharacterStore(CharacterSettings initial) : ICharacterSettingsStore
    {
        public CharacterSettings Value { get; private set; } = initial;
        public int SaveCount { get; private set; }
        public CharacterSettings Load() => Value;
        public void Save(CharacterSettings settings)
        {
            SaveCount++;
            Value = settings;
        }
    }

    private sealed class SessionNavigator : ISettingsNavigator
    {
        public IRelayCommand<PackageDetailRoute> OpenPackageCommand { get; } = new RelayCommand<PackageDetailRoute>(_ => { });
        public IRelayCommand BackToPackagesCommand { get; } = new RelayCommand(() => { });
        public IRelayCommand BackToAppearanceCommand { get; } = new RelayCommand(() => { });
        public void Select(SettingsSection section) { }
    }
}
