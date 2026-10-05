using System.IO;
using System.Windows;
using FgoPet.Core.Geometry;
using FgoPet.Core.Packs;
using FgoPet.Core.Portraits;
using FgoPet.Infrastructure.Packs;
using FgoPet.Kernel.Presentation;
using FgoPet.UiSdk;
using FgoPet.App.Runtime;

namespace FgoPet.App.Portraits.Live2D;

/// <summary>Uses a validated Cubism role-pack appearance when available, retaining Static as fallback.</summary>
public sealed class Live2DPortraitController : IPortraitBackend, IPortraitSurface, IPortraitTapSurface, IDisposable
{
    private readonly IPortraitSurface _static;
    private readonly IArtPackageRepository _repository;
    private readonly string _runtimeRoot;
    private readonly string _sessionsRoot;
    private readonly string _profileRoot;
    private Live2DSession? _session;
    private Live2DPortraitView? _view;
    private long _version;
    private bool _liveReady;
    private bool _disposed;
    private PortraitSelection? _selection;
    private ExpressionSemantic _expression;
    private bool _thinking;
    private double? _speechLevel;

    public Live2DPortraitController(
        IPortraitSurface staticSurface,
        IArtPackageRepository repository,
        string runtimeRoot,
        string sessionsRoot,
        string profileRoot)
    {
        _static = staticSurface;
        _repository = repository;
        _runtimeRoot = runtimeRoot;
        _sessionsRoot = sessionsRoot;
        _profileRoot = profileRoot;
        _static.StateChanged += OnStaticChanged;
    }

    public string BackendId => "builtin.live2d";
    public event EventHandler? StateChanged;
    public IPortraitFrame? CurrentFrame => _static.CurrentFrame is { } frame
        ? new Frame(frame, _view, _session, _liveReady,
            _session is null ? frame.Geometry : MashGeometry(frame.Geometry))
        : null;

    public FrameworkElement CreateView()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_view is not null) return _view;
        var view = new Live2DPortraitView(_profileRoot);
        view.Ready += OnViewReady;
        view.Failed += OnViewFailed;
        _view = view;
        view.SetExpression(ExpressionSemanticKeys.Key(_expression));
        view.SetThinking(_thinking);
        view.SetSpeechLevel(_speechLevel);
        return view;
    }

    public async Task ActivateAsync(PortraitSelection selection, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var version = Interlocked.Increment(ref _version);
        await _static.ActivateAsync(selection, cancellationToken);
        if (version != Volatile.Read(ref _version)) return;
        _selection = selection;
        _expression = ExpressionSemantic.Neutral;
        _thinking = false;
        _speechLevel = null;
        _view?.SetExpression("neutral");
        _view?.SetThinking(false);
        _view?.SetSpeechLevel(null);

        Live2DSession? next = null;
        try
        {
            var location = await _repository.GetAppearanceAsync(selection, cancellationToken);
            if (location is not null)
            {
                var manifest = AppearanceManifestReader.Read(Path.Combine(location.AppearanceRoot, "manifest.json"));
                if (manifest.Live2D is not null)
                {
                    next = await Task.Run(() => Live2DSession.TryCreate(
                        location.AppearanceRoot, manifest, _runtimeRoot, _sessionsRoot, cancellationToken), cancellationToken);
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or PackFailureException or InvalidOperationException)
        {
            // The already activated Static portrait remains usable.
        }
        if (version != Volatile.Read(ref _version))
        {
            next?.Dispose();
            return;
        }
        var old = Interlocked.Exchange(ref _session, next);
        _liveReady = false;
        StateChanged?.Invoke(this, EventArgs.Empty);
        old?.Dispose();
    }

    public void SetExpression(ExpressionSemantic semantic)
    {
        _static.SetExpression(semantic);
        _expression = semantic;
        _view?.SetExpression(ExpressionSemanticKeys.Key(semantic));
    }

    internal void SetThinking(bool active)
    {
        _thinking = active;
        _view?.SetThinking(active);
    }

    public void SetSpeechLevel(double? level)
    {
        if (_disposed || level is { } value && (!double.IsFinite(value) || value < 0 || value > 1)) return;
        _speechLevel = level;
        _view?.SetSpeechLevel(level);
    }

    public bool MatchesRole(ActiveRoleState role) => _selection is { } selection
        && selection.PackageId == role.PackageId && selection.AppearanceId == role.AppearanceId
        && selection.PackageVersion == role.PackageVersion;
    public void SetScale(double scale) => _static.SetScale(scale);
    public void ApplyDpi(Dpi2 dpi) => _static.ApplyDpi(dpi);
    public void OnPortraitTap()
    {
        if (_liveReady) _view?.PlayTap();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Interlocked.Increment(ref _version);
        _static.StateChanged -= OnStaticChanged;
        if (_view is not null)
        {
            _view.Ready -= OnViewReady;
            _view.Failed -= OnViewFailed;
            _view.Dispose();
        }
        Interlocked.Exchange(ref _session, null)?.Dispose();
    }

    private void OnStaticChanged(object? sender, EventArgs args) => StateChanged?.Invoke(this, EventArgs.Empty);

    private void OnViewReady(Live2DHitMask mask)
    {
        if (_disposed || _session is null) return;
        _liveReady = true;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnViewFailed()
    {
        if (_disposed) return;
        _liveReady = false;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    internal static PortraitGeometry MashGeometry(PortraitGeometry staticGeometry)
    {
        // The paired static Mash portrait is 303x603. Keep its height/scale and
        // Reserve 24 source pixels per side and 72 above the original 483x603
        // viewport. The renderer retains the original character scale, bottom aligned.
        var scale = staticGeometry.LogicalSize.Height / 603.0;
        var dpi = new Dpi2(
            staticGeometry.BodyDeviceRect.Width / staticGeometry.BodyLogicalRect.Width,
            staticGeometry.BodyDeviceRect.Height / staticGeometry.BodyLogicalRect.Height);
        return PortraitLayout.Calculate(
            new PortraitSourceGeometry(531, 675, 127, 72, 256, 240, 265, 432), scale, dpi);
    }

    private sealed record Frame(
        IPortraitFrame Static,
        Live2DPortraitView? View,
        Live2DSession? Session,
        bool LiveReady,
        PortraitGeometry FrameGeometry) : IPortraitFrame
    {
        public PortraitGeometry Geometry => FrameGeometry;

        public void Present(FrameworkElement view)
        {
            if (view is not Live2DPortraitView portrait) throw new ArgumentException("Incompatible Live2D portrait view.", nameof(view));
            portrait.Present(Static, Session, LiveReady);
        }

        public bool IsHit(Point portraitLocalPoint)
        {
            if (LiveReady && View?.HitMask is { } mask) return mask.IsHit(portraitLocalPoint, Geometry);
            var inset = (Geometry.LogicalSize.Width - Static.Geometry.LogicalSize.Width) / 2;
            var topInset = Geometry.LogicalSize.Height - Static.Geometry.LogicalSize.Height;
            return Static.IsHit(new Point(portraitLocalPoint.X - inset, portraitLocalPoint.Y - topInset));
        }
    }
}
