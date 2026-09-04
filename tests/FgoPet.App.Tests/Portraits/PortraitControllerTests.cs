using System.IO;
using FgoPet.App.Portraits;
using FgoPet.App.Runtime;
using FgoPet.Core.Geometry;
using FgoPet.Core.Packs;
using FgoPet.Core.Portraits;
using Xunit;

namespace FgoPet.App.Tests.Portraits;

public sealed class PortraitControllerTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "fgo-pet-ctrl-" + Guid.NewGuid().ToString("N"));
    private readonly FakeRepository _repository = new();
    private readonly PortraitSnapshotCache _cache = new();
    private readonly PortraitController _controller;

    public PortraitControllerTests()
    {
        Directory.CreateDirectory(_temp);
        _controller = new PortraitController(_repository, new ExpressionResolver(), _cache, new Dpi2(2.0, 2.0));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
            // best effort cleanup
        }
    }

    [Fact]
    public async Task ActivateAsync_publishes_state_and_marks_last_known_good()
    {
        var bundle = WriteBundle("a");
        _repository.Get = _ => Task.FromResult<AppearanceLocation?>(Location("pkg", "a", bundle.Root));
        var changes = 0;
        _controller.StateChanged += (_, _) => changes++;

        await _controller.ActivateAsync(new PortraitSelection("pkg", "a"), CancellationToken.None);

        var state = _controller.CurrentState;
        Assert.NotNull(state);
        Assert.Equal(new PortraitSelection("pkg", "a"), state!.Selection);
        Assert.True(state.Snapshot.Body.IsFrozen);
        Assert.Equal(1, changes);
        Assert.Single(_repository.LastKnownGoods);
    }

    [Fact]
    public async Task ActivateAsync_projects_the_published_state_to_app_runtime()
    {
        var bundle = WriteBundle("runtime-state");
        _repository.Get = _ => Task.FromResult<AppearanceLocation?>(Location("pkg", "runtime-state", bundle.Root));
        var runtime = new AppRuntime();
        var controller = new PortraitController(_repository, new ExpressionResolver(), _cache, new Dpi2(2.0, 2.0), runtime);

        await controller.ActivateAsync(new PortraitSelection("pkg", "runtime-state"), CancellationToken.None);

        Assert.Same(controller.CurrentState, runtime.Portrait);
    }

    [Fact]
    public async Task ActivateAsync_keeps_running_when_last_known_good_persistence_is_unavailable()
    {
        var bundle = WriteBundle("persistence-unavailable");
        _repository.Get = _ => Task.FromResult<AppearanceLocation?>(Location("pkg", "persistence-unavailable", bundle.Root));
        _repository.ThrowOnLastKnownGood = true;

        await _controller.ActivateAsync(new PortraitSelection("pkg", "persistence-unavailable"), CancellationToken.None);

        Assert.NotNull(_controller.CurrentState);
    }

    [Fact]
    public async Task A_failed_candidate_preserves_the_old_state()
    {
        var valid = WriteBundle("valid");
        _repository.Get = _ => Task.FromResult<AppearanceLocation?>(Location("pkg", "a", valid.Root));
        await _controller.ActivateAsync(new PortraitSelection("pkg", "a"), CancellationToken.None);
        var previous = _controller.CurrentState;
        var changes = 0;
        _controller.StateChanged += (_, _) => changes++;

        // Break the second bundle's content after it was written.
        var broken = WriteBundle("broken");
        File.WriteAllBytes(Path.Combine(broken.Root, "runtime", "expressions", "r01c01.png"), new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });
        _repository.Get = _ => Task.FromResult<AppearanceLocation?>(Location("pkg", "b", broken.Root));

        await Assert.ThrowsAsync<PackFailureException>(() =>
            _controller.ActivateAsync(new PortraitSelection("pkg", "b"), CancellationToken.None));

        Assert.Same(previous, _controller.CurrentState);
        Assert.Equal(0, changes);
        Assert.Single(_repository.LastKnownGoods);
    }

    [Fact]
    public async Task No_state_is_published_while_loading()
    {
        var valid = WriteBundle("a");
        _repository.Get = _ => Task.FromResult<AppearanceLocation?>(Location("pkg", "a", valid.Root));
        await _controller.ActivateAsync(new PortraitSelection("pkg", "a"), CancellationToken.None);
        var previous = _controller.CurrentState;

        var gate = new TaskCompletionSource<AppearanceLocation?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _repository.Get = _ => gate.Task;
        var loading = _controller.ActivateAsync(new PortraitSelection("pkg", "b"), CancellationToken.None);

        Assert.Same(previous, _controller.CurrentState);
        gate.SetResult(Location("pkg", "b", WriteBundle("b").Root));
        await loading;
        Assert.NotSame(previous, _controller.CurrentState);
    }

    [Fact]
    public async Task SetExpression_preserves_body_and_geometry()
    {
        var bundle = WriteBundleWithSecondExpression("a");
        _repository.Get = _ => Task.FromResult<AppearanceLocation?>(Location("pkg", "a", bundle.Root));
        await _controller.ActivateAsync(new PortraitSelection("pkg", "a"), CancellationToken.None);
        var before = _controller.CurrentState!;

        _controller.SetExpression(ExpressionSemantic.Sad);

        var after = _controller.CurrentState!;
        Assert.Same(before.Snapshot, after.Snapshot);
        Assert.Same(before.Geometry, after.Geometry);
        Assert.Equal(ExpressionSemantic.Sad, after.Semantic);
        Assert.Equal("r01c02", after.ExpressionAssetId);
    }

    [Fact]
    public async Task SetScale_recomputes_geometry_and_rejects_invalid_scales()
    {
        var bundle = WriteBundle("a");
        _repository.Get = _ => Task.FromResult<AppearanceLocation?>(Location("pkg", "a", bundle.Root));
        await _controller.ActivateAsync(new PortraitSelection("pkg", "a"), CancellationToken.None);
        var originalGeometry = _controller.CurrentState!.Geometry;

        _controller.SetScale(0.75);

        var scaled = _controller.CurrentState!;
        Assert.Equal(0.75, scaled.Scale);
        Assert.NotSame(originalGeometry, scaled.Geometry);
        Assert.Throws<ArgumentOutOfRangeException>(() => _controller.SetScale(0.7));
    }

    [Fact]
    public async Task ApplyDpi_recomputes_all_geometry_without_changing_selection_or_scale()
    {
        var bundle = WriteBundle("dpi");
        _repository.Get = _ => Task.FromResult<AppearanceLocation?>(Location("pkg", "dpi", bundle.Root));
        await _controller.ActivateAsync(new PortraitSelection("pkg", "dpi"), CancellationToken.None);
        var before = _controller.CurrentState!;

        _controller.ApplyDpi(new Dpi2(1.5, 2.0));

        var after = _controller.CurrentState!;
        Assert.Equal(before.Selection, after.Selection);
        Assert.Equal(before.Scale, after.Scale);
        Assert.NotEqual(before.Geometry.DeviceSize, after.Geometry.DeviceSize);
        Assert.Equal(Math.Round(13 * before.Scale * 1.5), after.Geometry.OverlayDeviceRect.X);
        Assert.Equal(Math.Round(360 * before.Scale * 2.0), after.Geometry.PanelAnchorDevice.Y);
    }

    [Fact]
    public async Task ApplyDpi_before_first_activation_is_used_for_initial_geometry()
    {
        var bundle = WriteBundle("initial-dpi");
        _repository.Get = _ => Task.FromResult<AppearanceLocation?>(Location("pkg", "initial-dpi", bundle.Root));
        _controller.ApplyDpi(new Dpi2(2.0, 2.0));

        await _controller.ActivateAsync(new PortraitSelection("pkg", "initial-dpi"), CancellationToken.None);

        Assert.Equal(303, _controller.CurrentState!.Geometry.DeviceSize.Width);
        Assert.Equal(603, _controller.CurrentState.Geometry.DeviceSize.Height);
    }

    [Fact]
    public async Task Loading_a_third_appearance_evicts_the_oldest_snapshot()
    {
        _repository.Get = selection => Task.FromResult<AppearanceLocation?>(
            Location(selection.PackageId, selection.AppearanceId, WriteBundle(selection.AppearanceId).Root));

        var a = new PortraitSelection("pkg", "a");
        var b = new PortraitSelection("pkg", "b");
        var c = new PortraitSelection("pkg", "c");
        await _controller.ActivateAsync(a, CancellationToken.None);
        await _controller.ActivateAsync(b, CancellationToken.None);
        await _controller.ActivateAsync(c, CancellationToken.None);

        Assert.Null(_cache.TryGet(a));
        Assert.NotNull(_cache.TryGet(b));
        Assert.NotNull(_cache.TryGet(c));
    }

    private (string Root, string ManifestPath) WriteBundle(string folder)
    {
        var root = Path.Combine(_temp, folder);
        return AppearanceBundle.Write(
            root,
            AppearanceBundle.CreatePng(303, 603, alpha: 255),
            AppearanceBundle.CreatePng(256, 240, alpha: 200));
    }

    private (string Root, string ManifestPath) WriteBundleWithSecondExpression(string folder)
    {
        var root = Path.Combine(_temp, folder);
        var bundle = AppearanceBundle.Write(
            root,
            AppearanceBundle.CreatePng(303, 603, alpha: 255),
            AppearanceBundle.CreatePng(256, 240, alpha: 200),
            expressionPng2: AppearanceBundle.CreatePng(256, 240, alpha: 200));
        var manifestJson = File.ReadAllText(bundle.ManifestPath)
            .Replace("\"sad\": \"r01c01\"", "\"sad\": \"r01c02\"", StringComparison.Ordinal);
        File.WriteAllText(bundle.ManifestPath, manifestJson);
        return bundle;
    }

    private static AppearanceLocation Location(string packageId, string appearanceId, string root) =>
        new(new PackIdentity(packageId, "1.0.0"), appearanceId, root);

    private sealed class FakeRepository : IArtPackageRepository
    {
        public Func<PortraitSelection, Task<AppearanceLocation?>> Get { get; set; } =
            _ => Task.FromResult<AppearanceLocation?>(null);

        public List<PortraitSelection> LastKnownGoods { get; } = new();

        public bool ThrowOnLastKnownGood { get; set; }

        public Task<PackCatalog> ScanAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new PackCatalog(Array.Empty<InstalledPack>()));

        public Task<IReadOnlyList<InstalledServant>> ListServantsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<InstalledServant>>(Array.Empty<InstalledServant>());

        public Task<AppearanceLocation?> GetAppearanceAsync(PortraitSelection selection, CancellationToken cancellationToken) =>
            Get(selection);

        public Task<AppearanceLocation?> ResolveStartupSelectionAsync(PortraitSelection? requested, CancellationToken cancellationToken) =>
            requested is null ? Task.FromResult<AppearanceLocation?>(null) : Get(requested);

        public Task<bool> RemoveAsync(string packageId, string packageVersion, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task MarkLastKnownGoodAsync(PortraitSelection selection, CancellationToken cancellationToken)
        {
            if (ThrowOnLastKnownGood)
            {
                throw new UnauthorizedAccessException("test persistence failure");
            }

            LastKnownGoods.Add(selection);
            return Task.CompletedTask;
        }
    }
}
