using FgoPet.App.Servants;
using FgoPet.Core.Packs;
using FgoPet.Core.Portraits;
using FgoPet.Core.Settings;
using Microsoft.Extensions.Logging;

namespace FgoPet.App.Bootstrap;

public interface IDesktopAppUi
{
    void InitializeTray();
    void ShowLibrary(string? offeredPackPath = null);
    void ShowPortrait();
}

/// <summary>
/// Connects persisted selection and installed packs to the desktop UI. The tray is
/// initialized first as in Phase 1; then Phase 2 attempts migration and focus
/// recovery before the portrait shows. Any Phase 2 failure degrades to
/// Phase-1-only behavior (portrait/library still work, focus timer disabled).
/// </summary>
public sealed class DesktopAppShell : IAppShell
{
    private readonly IArtPackageRepository _repository;
    private readonly IPortraitController _controller;
    private readonly IAppSettingsStore _settings;
    private readonly IDesktopAppUi _ui;
    private readonly IRuntimeDatabaseMigrator? _migrator;
    private readonly IFocusRestorer? _restorer;
    private readonly IPhase2Availability? _phase2;
    private readonly Func<ServantFocusConnector>? _connectorFactory;

    public DesktopAppShell(
        IArtPackageRepository repository,
        IPortraitController controller,
        IAppSettingsStore settings,
        IDesktopAppUi ui,
        IRuntimeDatabaseMigrator? migrator = null,
        IFocusRestorer? restorer = null,
        IPhase2Availability? phase2 = null,
        Func<ServantFocusConnector>? connectorFactory = null,
        ILogger<DesktopAppShell>? logger = null)
    {
        _repository = repository;
        _controller = controller;
        _settings = settings;
        _ui = ui;
        _migrator = migrator;
        _restorer = restorer;
        _phase2 = phase2;
        _connectorFactory = connectorFactory;
        _logger = logger;
    }

    private readonly ILogger<DesktopAppShell>? _logger;

    public async Task StartAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        // 1. Tray first, exactly as Phase 1.
        _ui.InitializeTray();

        // 2-3. Phase 2 runtime: migrate then restore, degrading on any failure.
        InitializePhase2Runtime();

        var offeredPack = arguments.FirstOrDefault(path =>
            path.EndsWith(".fgopetpack", StringComparison.OrdinalIgnoreCase));
        if (offeredPack is not null)
        {
            _ui.ShowLibrary(offeredPack);
            return;
        }

        var requested = _settings.Load().Selection;
        var location = await _repository.ResolveStartupSelectionAsync(requested, cancellationToken);
        if (location is null)
        {
            _ui.ShowLibrary();
            return;
        }

        var resolved = new PortraitSelection(
            location.Identity.PackageId,
            location.AppearanceId,
            location.Identity.PackageVersion);

        // Activation goes through the connector so the stable servant ID is resolved
        // from the resolved pack (needed by the focus start command and bond credit).
        if (_connectorFactory is not null)
        {
            await _connectorFactory().ActivateAsync(resolved, cancellationToken);
        }
        else
        {
            await _controller.ActivateAsync(resolved, cancellationToken);
        }

        // 4. Portrait last.
        _ui.ShowPortrait();
    }

    private void InitializePhase2Runtime()
    {
        if (_migrator is null || _restorer is null || _phase2 is null)
        {
            return;
        }

        try
        {
            _migrator.Migrate();
            _restorer.Restore();
        }
        catch (Exception error)
        {
            // Log only the exception type and safe message; no absolute paths.
            _logger?.LogError(error, "Phase 2 runtime initialization failed: {ExceptionType}", error.GetType().Name);
            _phase2.MarkUnavailable();
        }
    }
}
