using FgoPet.App.Servants;
using FgoPet.Character.Settings;
using FgoPet.Core.Packs;
using FgoPet.Core.Portraits;
using FgoPet.Extensibility;
using Microsoft.Extensions.Logging;

namespace FgoPet.App.Bootstrap;

public interface IDesktopAppUi
{
    void InitializeTray();
    void ShowLibrary(string? offeredPackPath = null);
    void ShowFirstStartChat();
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
    private readonly ICharacterSettingsStore _characterSettings;
    private readonly IDesktopAppUi _ui;
    private readonly IRuntimeDatabaseMigrator? _migrator;
    private readonly IFocusRestorer? _restorer;
    private readonly IPhase2Availability? _phase2;
    private readonly IRoleActivationService? _activation;

    public DesktopAppShell(
        IArtPackageRepository repository,
        IPortraitController controller,
        ICharacterSettingsStore characterSettings,
        IDesktopAppUi ui,
        IRuntimeDatabaseMigrator? migrator = null,
        IFocusRestorer? restorer = null,
        IPhase2Availability? phase2 = null,
        ILogger<DesktopAppShell>? logger = null,
        PluginRuntime? plugins = null,
        IRoleActivationService? activation = null)
    {
        _repository = repository;
        _controller = controller;
        _characterSettings = characterSettings;
        _ui = ui;
        _migrator = migrator;
        _restorer = restorer;
        _phase2 = phase2;
        _activation = activation;
        _logger = logger;
        _plugins = plugins;
    }

    private readonly ILogger<DesktopAppShell>? _logger;
    private readonly PluginRuntime? _plugins;

    public async Task StartAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        // 1. Tray first, exactly as Phase 1.
        _ui.InitializeTray();

        // 2-3. Phase 2 runtime: migrate then restore, degrading on any failure.
        InitializePhase2Runtime();
        if (_plugins is not null)
            await _plugins.NotifyApplicationReadyAsync(new(_phase2?.IsAvailable != false), cancellationToken);

        var offeredPack = arguments.FirstOrDefault(path =>
            path.EndsWith(".fgopetpack", StringComparison.OrdinalIgnoreCase));
        if (offeredPack is not null)
        {
            _ui.ShowLibrary(offeredPack);
            return;
        }

        if (_activation is not null)
        {
            var firstStartForActivation = _characterSettings.Load().Selection is null;
            var restored = await _activation.RestoreAsync(cancellationToken).ConfigureAwait(true);
            if (restored.Succeeded)
            {
                _ui.ShowPortrait();
                if (firstStartForActivation)
                {
                    _ui.ShowFirstStartChat();
                }
            }
            else
            {
                _ui.ShowLibrary();
            }

            return;
        }

        var requested = _characterSettings.Load().Selection;
        var firstStart = requested is null;
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

        await _controller.ActivateAsync(resolved, cancellationToken);

        // 4. Portrait last.
        _ui.ShowPortrait();
        if (firstStart)
        {
            _ui.ShowFirstStartChat();
        }
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
