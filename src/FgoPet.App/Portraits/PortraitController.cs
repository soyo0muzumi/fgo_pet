using System.IO;
using FgoPet.Core.Geometry;
using FgoPet.Core.Packs;
using FgoPet.Core.Portraits;
using FgoPet.Infrastructure.Packs;

namespace FgoPet.App.Portraits;

/// <summary>
/// Owns the currently active portrait. Activation is two-phase: the whole snapshot and
/// geometry are built and validated off the UI thread, then the immutable state is
/// swapped in one shot on the caller's context. A failure leaves the previous state
/// untouched, and a successful activation marks the selection as last-known-good.
/// </summary>
public sealed class PortraitController : IPortraitController
{
    private static readonly IReadOnlySet<double> AllowedScales = new HashSet<double> { 0.50, 0.60, 0.75 };

    private readonly IArtPackageRepository _repository;
    private readonly IExpressionResolver _resolver;
    private readonly PortraitSnapshotCache _cache;
    private Dpi2 _dpi;
    private long _operationVersion;
    private AppearanceManifestV3? _appearance;
    private double _scale = 0.50;

    public PortraitController(
        IArtPackageRepository repository,
        IExpressionResolver resolver,
        PortraitSnapshotCache cache,
        Dpi2 dpi)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _dpi = dpi;
    }

    public event EventHandler? StateChanged;

    /// <summary>The last successfully published state, or null before the first activation.</summary>
    public PortraitState? CurrentState { get; private set; }

    public async Task ActivateAsync(PortraitSelection selection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var version = ++_operationVersion;

        var location = await _repository.GetAppearanceAsync(selection, cancellationToken).ConfigureAwait(false)
            ?? throw new PackFailureException(new PackFailure(
                PackErrorCode.AssetMissing,
                $"外观 '{selection.AppearanceId}' 未安装。",
                null));

        var (state, appearance) = await Task.Run(() => BuildState(location, selection, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
        if (version != _operationVersion)
        {
            return; // superseded by a newer activation
        }

        CurrentState = state;
        _appearance = appearance;
        _cache.Put(selection, state.Snapshot);
        StateChanged?.Invoke(this, EventArgs.Empty);

        await _repository.MarkLastKnownGoodAsync(selection, cancellationToken).ConfigureAwait(false);
    }

    public void SetExpression(ExpressionSemantic semantic)
    {
        var appearance = _appearance ?? throw new PackFailureException(new PackFailure(
            PackErrorCode.ExpressionMappingInvalid,
            "尚未激活任何画像。"));
        var state = CurrentState ?? throw new InvalidOperationException("尚未激活任何画像。");

        var resolution = _resolver.Resolve(semantic, appearance);
        Publish(state with { Semantic = semantic, ExpressionAssetId = resolution.AssetId });
    }

    public void SetScale(double scale)
    {
        if (!AllowedScales.Contains(scale))
        {
            throw new ArgumentOutOfRangeException(nameof(scale), scale, "仅支持 0.50 / 0.60 / 0.75。");
        }
        var state = CurrentState ?? throw new InvalidOperationException("尚未激活任何画像。");

        _scale = scale;
        var geometry = PortraitLayout.Calculate(state.Snapshot.SourceGeometry, scale, _dpi);
        Publish(state with { Scale = scale, Geometry = geometry });
    }

    /// <summary>Recomputes geometry after a DPI or display change without altering the scale.</summary>
    public void ApplyDpi(Dpi2 dpi)
    {
        _dpi = dpi;
        var state = CurrentState;
        if (state is null)
        {
            return;
        }
        Publish(state with { Geometry = PortraitLayout.Calculate(state.Snapshot.SourceGeometry, _scale, dpi) });
    }

    private (PortraitState State, AppearanceManifestV3 Appearance) BuildState(
        AppearanceLocation location,
        PortraitSelection selection,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var appearance = ReadAppearance(location);
        var snapshot = LoadSnapshot(location, appearance);
        var geometry = PortraitLayout.Calculate(snapshot.SourceGeometry, _scale, _dpi);
        var resolution = _resolver.Resolve(ExpressionSemantic.Neutral, appearance);
        var state = new PortraitState(selection, ExpressionSemantic.Neutral, resolution.AssetId, _scale, snapshot, geometry);
        return (state, appearance);
    }

    private static AppearanceManifestV3 ReadAppearance(AppearanceLocation location)
    {
        var manifestPath = Path.Combine(location.AppearanceRoot, "manifest.json");
        return AppearanceManifestReader.Read(manifestPath);
    }

    private static PortraitSnapshot LoadSnapshot(AppearanceLocation location, AppearanceManifestV3 appearance)
    {
        var validation = AppearanceValidator.Validate(appearance, location.AppearanceRoot);
        if (!validation.IsValid)
        {
            throw new PackFailureException(validation.Errors[0]);
        }
        return BitmapAssetLoader.LoadValidated(validation.Value!);
    }

    private void Publish(PortraitState state)
    {
        CurrentState = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
