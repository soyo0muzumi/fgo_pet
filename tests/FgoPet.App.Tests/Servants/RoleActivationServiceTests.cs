using FgoPet.App.Runtime;
using FgoPet.App.Servants;
using FgoPet.Core.Geometry;
using FgoPet.Core.Packs;
using FgoPet.Core.Portraits;
using FgoPet.Core.Settings;
using Xunit;

namespace FgoPet.App.Tests.Servants;

public sealed class RoleActivationServiceTests
{
    private static readonly PortraitSelection Selection = new("pack", "casual", "1.0.0");

    [Fact]
    public async Task ActivateAsync_updates_role_state_after_portrait_activation()
    {
        var settings = new FakeSettingsStore();
        var runtime = new AppRuntime();
        var service = CreateService(settings, runtime);

        var result = await service.ActivateAsync(Selection, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("mash_kyrielight", runtime.ActiveRole!.ServantId);
        Assert.Equal(Selection, settings.Load().Selection);
    }

    [Fact]
    public async Task ActivateAsync_does_not_update_settings_when_portrait_activation_fails()
    {
        var settings = new FakeSettingsStore();
        var runtime = new AppRuntime();
        var service = CreateService(settings, runtime, new FakePortraitController { ShouldFail = true });

        var result = await service.ActivateAsync(Selection, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Null(runtime.ActiveRole);
        Assert.Null(settings.Load().Selection);
    }

    [Fact]
    public async Task RestoreAsync_returns_missing_package_when_saved_selection_cannot_be_resolved()
    {
        var settings = new FakeSettingsStore { Current = AppSettings.Defaults with { Selection = Selection } };
        var service = new RoleActivationService(
            new FakeRepository(null),
            new FakePortraitController(),
            settings,
            new AppRuntime());

        var result = await service.RestoreAsync(CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(RoleActivationFailure.MissingPackage, result.Failure);
    }

    private static RoleActivationService CreateService(
        FakeSettingsStore settings,
        AppRuntime runtime,
        FakePortraitController? controller = null,
        AppearanceLocation? resolved = null)
    {
        return new RoleActivationService(
            new FakeRepository(resolved ?? new AppearanceLocation(new PackIdentity("pack", "1.0.0"), "casual", "C:\\pack")),
            controller ?? new FakePortraitController(),
            settings,
            runtime);
    }

    private sealed class FakeSettingsStore : IAppSettingsStore
    {
        public string Location => "memory";
        public AppSettings Current { get; set; } = AppSettings.Defaults;
        public AppSettings Load() => Current;
        public void Save(AppSettings settings) => Current = settings;
    }

    private sealed class FakeRepository(AppearanceLocation? resolved) : IArtPackageRepository
    {
        public Task<PackCatalog> ScanAsync(CancellationToken cancellationToken) => Task.FromResult(new PackCatalog([]));
        public Task<IReadOnlyList<InstalledServant>> ListServantsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<InstalledServant>>([new InstalledServant(
                "pack", "mash_kyrielight", "玛修", null, "local", [new ServantAppearance("casual", "1.0.0", "C:\\pack\\appearances\\casual", null)])]);
        public Task<AppearanceLocation?> GetAppearanceAsync(PortraitSelection selection, CancellationToken cancellationToken) => Task.FromResult(resolved);
        public Task<AppearanceLocation?> ResolveStartupSelectionAsync(PortraitSelection? selection, CancellationToken cancellationToken) => Task.FromResult(resolved);
        public Task<bool> RemoveAsync(string packageId, string packageVersion, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task MarkLastKnownGoodAsync(PortraitSelection selection, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakePortraitController : IPortraitController
    {
        public bool ShouldFail { get; set; }
        public Task ActivateAsync(PortraitSelection selection, CancellationToken cancellationToken) =>
            ShouldFail ? Task.FromException(new InvalidOperationException("activation failed")) : Task.CompletedTask;
        public void SetExpression(ExpressionSemantic semantic) { }
        public void SetScale(double scale) { }
        public void ApplyDpi(Dpi2 dpi) { }
    }
}
