using FgoPet.App.Bootstrap;
using FgoPet.Core.Packs;
using FgoPet.Core.Portraits;
using FgoPet.Core.Settings;
using FgoPet.Core.Agents;
using Xunit;

namespace FgoPet.App.Tests.Bootstrap;

public sealed class DesktopAppShellTests
{
    [Fact]
    public async Task Disabled_startup_notifies_runtime_once_without_waiting_for_it()
    {
        var runtime = new PendingRuntime();
        var ui = new FakeUi();
        var shell = new DesktopAppShell(new FakeRepository(null), new FakeController(), new FakeSettings(), ui, agentRuntime: runtime);
        await shell.StartAsync([], CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        await shell.StartAsync([], CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(ui.LibraryShown);
        Assert.Equal(1, runtime.Calls);
        Assert.False(runtime.Enabled);
        runtime.Complete();
    }

    private sealed class PendingRuntime : IAgentRelayRuntime
    {
        private readonly TaskCompletionSource _pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }
        public bool Enabled { get; private set; }
        public AgentRelaySnapshot Current => AgentRelaySnapshot.Disabled;
        public event Action<AgentRelaySnapshot>? SnapshotChanged { add { } remove { } }
        public Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
        { Calls++; Enabled = enabled; return _pending.Task; }
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Complete() => _pending.TrySetResult();
    }

    [Fact]
    public async Task Start_without_installed_pack_keeps_tray_and_shows_library()
    {
        var ui = new FakeUi();
        var shell = new DesktopAppShell(new FakeRepository(null), new FakeController(), new FakeSettings(), ui);

        await shell.StartAsync([], CancellationToken.None);

        Assert.True(ui.TrayInitialized);
        Assert.True(ui.LibraryShown);
        Assert.False(ui.PortraitShown);
    }

    [Fact]
    public async Task Start_with_valid_selection_activates_and_shows_portrait()
    {
        var selection = new PortraitSelection("official.mash", "casual", "1.0.0");
        var controller = new FakeController();
        var shell = new DesktopAppShell(new FakeRepository(new AppearanceLocation(new PackIdentity("official.mash", "1.0.0"), "casual", "C:\\pack")), controller, new FakeSettings(selection), new FakeUi());

        await shell.StartAsync([], CancellationToken.None);

        Assert.Equal(selection, controller.Activated);
    }

    [Fact]
    public async Task Start_with_pack_argument_opens_it_in_library_without_auto_activation()
    {
        var ui = new FakeUi();
        var shell = new DesktopAppShell(new FakeRepository(null), new FakeController(), new FakeSettings(), ui);

        await shell.StartAsync(["C:\\Downloads\\mash.fgopetpack"], CancellationToken.None);

        Assert.Equal("C:\\Downloads\\mash.fgopetpack", ui.OfferedPack);
        Assert.True(ui.LibraryShown);
    }

    private sealed class FakeUi : IDesktopAppUi
    {
        public bool TrayInitialized { get; private set; }
        public bool LibraryShown { get; private set; }
        public bool PortraitShown { get; private set; }
        public string? OfferedPack { get; private set; }
        public void InitializeTray() => TrayInitialized = true;
        public void ShowLibrary(string? offeredPackPath = null) { LibraryShown = true; OfferedPack = offeredPackPath; }
        public void ShowPortrait() => PortraitShown = true;
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
