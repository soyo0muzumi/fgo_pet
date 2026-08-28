using FgoPet.App.Bootstrap;
using FgoPet.Core.Packs;
using FgoPet.Core.Portraits;
using FgoPet.Core.Settings;
using FgoPet.Infrastructure.Persistence;
using Xunit;

namespace FgoPet.App.Tests.Bootstrap;

public sealed class Phase2StartupTests
{
    [Fact]
    public async Task Startup_migrates_runtime_database_before_restoring_focus()
    {
        var calls = new List<string>();
        var migrator = new FakeMigrator { OnMigrate = () => calls.Add("migrate") };
        var restorer = new FakeFocusRestorer { OnRestore = () => calls.Add("restore") };
        var ui = new RecordingUi { OnInit = () => calls.Add("tray") };
        ui.OnPortrait = () => calls.Add("portrait");
        var shell = new DesktopAppShell(
            new FakeRepository(new AppearanceLocation(new PackIdentity("official.mash", "1.0.0"), "casual", "C:\\pack")),
            new FakeController(), new FakeSettings(),
            ui, migrator, restorer, new FakePhase2Availability());

        await shell.StartAsync([], CancellationToken.None);

        Assert.Equal(new[] { "tray", "migrate", "restore", "portrait" }, calls.Take(4));
    }

    [Fact]
    public async Task Migration_failure_keeps_phase1_portrait_available_and_disables_phase2()
    {
        var migrator = new FakeMigrator { Exception = new RuntimeDatabaseVersionException(99, 1) };
        var availability = new FakePhase2Availability();
        var ui = new RecordingUi();
        var shell = new DesktopAppShell(
            new FakeRepository(new AppearanceLocation(new PackIdentity("official.mash", "1.0.0"), "casual", "C:\\pack")),
            new FakeController(), new FakeSettings(), ui, migrator, new FakeFocusRestorer(), availability);

        await shell.StartAsync([], CancellationToken.None);

        Assert.True(ui.PortraitShown);
        Assert.False(availability.IsAvailable);
    }

    [Fact]
    public async Task Restore_failure_disables_phase2_but_keeps_portrait_startup()
    {
        var ui = new RecordingUi();
        var availability = new FakePhase2Availability();
        var shell = new DesktopAppShell(
            new FakeRepository(new AppearanceLocation(new PackIdentity("official.mash", "1.0.0"), "casual", "C:\\pack")),
            new FakeController(), new FakeSettings(), ui,
            new FakeMigrator(), new FakeFocusRestorer { Exception = new InvalidOperationException("corrupt row") },
            availability);

        await shell.StartAsync([], CancellationToken.None);

        Assert.True(ui.PortraitShown);
        Assert.False(availability.IsAvailable);
    }

    private sealed class RecordingUi : IDesktopAppUi
    {
        public bool TrayInitialized { get; private set; }
        public bool LibraryShown { get; private set; }
        public bool PortraitShown { get; private set; }
        public Action? OnInit { get; set; }
        public Action? OnPortrait { get; set; }
        public void InitializeTray() { OnInit?.Invoke(); TrayInitialized = true; }
        public void ShowLibrary(string? offeredPackPath = null) => LibraryShown = true;
        public void ShowPortrait() { PortraitShown = true; OnPortrait?.Invoke(); }
    }

    private sealed class FakeMigrator : IRuntimeDatabaseMigrator
    {
        public Action? OnMigrate { get; set; }
        public Exception? Exception { get; set; }
        public void Migrate()
        {
            if (Exception is not null)
            {
                throw Exception;
            }

            OnMigrate?.Invoke();
        }
    }

    private sealed class FakeFocusRestorer : IFocusRestorer
    {
        public Action? OnRestore { get; set; }
        public Exception? Exception { get; set; }
        public void Restore()
        {
            if (Exception is not null)
            {
                throw Exception;
            }

            OnRestore?.Invoke();
        }
    }

    private sealed class FakePhase2Availability : IPhase2Availability
    {
        public bool IsAvailable { get; private set; } = true;
        public void MarkUnavailable() => IsAvailable = false;
    }

    private sealed class FakeSettings(PortraitSelection? selection = null) : IAppSettingsStore
    {
        public string Location => "settings.json";
        public AppSettings Load() => AppSettings.Defaults with { Selection = selection };
        public void Save(AppSettings settings) { }
    }

    private sealed class FakeController : IPortraitController
    {
        public PortraitSelection? Activated { get; private set; }
        public Task ActivateAsync(PortraitSelection selection, CancellationToken cancellationToken) { Activated = selection; return Task.CompletedTask; }
        public void SetExpression(ExpressionSemantic semantic) { }
        public void SetScale(double scale) { }
        public void ApplyDpi(Core.Geometry.Dpi2 dpi) { }
    }

    private sealed class FakeRepository(AppearanceLocation? resolved) : IArtPackageRepository
    {
        public Task<PackCatalog> ScanAsync(CancellationToken cancellationToken) => Task.FromResult(new PackCatalog([]));
        public Task<IReadOnlyList<InstalledServant>> ListServantsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<InstalledServant>>([]);
        public Task<AppearanceLocation?> GetAppearanceAsync(PortraitSelection selection, CancellationToken cancellationToken) => Task.FromResult(resolved);
        public Task<AppearanceLocation?> ResolveStartupSelectionAsync(PortraitSelection? requested, CancellationToken cancellationToken) => Task.FromResult(resolved);
        public Task<bool> RemoveAsync(string packageId, string packageVersion, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task MarkLastKnownGoodAsync(PortraitSelection selection, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
