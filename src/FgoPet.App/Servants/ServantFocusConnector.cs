using System.Globalization;
using System.IO;
using FgoPet.App.Bootstrap;
using FgoPet.App.Feedback;
using FgoPet.App.Focus;
using FgoPet.App.Panels;
using FgoPet.App.Portraits;
using FgoPet.Core.Events;
using FgoPet.Core.Focus;
using FgoPet.Core.Packs;
using FgoPet.Core.Portraits;
using FgoPet.Infrastructure.Packs;
using Microsoft.Extensions.Logging;

namespace FgoPet.App.Servants;

/// <summary>
/// Phase 2 glue: resolves the stable servant ID from the active resolved pack,
/// keeps the panel ViewModel's servant/feedback surfaces current, selects package
/// dialogue for focus events, and applies expressions through the portrait
/// controller. Dialogue failures fall back neutrally and never interrupt the timer.
/// </summary>
public sealed class ServantFocusConnector
{
    private readonly IArtPackageRepository _repository;
    private readonly IFocusSessionService _focus;
    private readonly AttachedPanelViewModel _panel;
    private readonly PortraitController _controller;
    private readonly EventFeedbackSelector _selector;
    private readonly IPhase2Availability _availability;
    private readonly ILogger<ServantFocusConnector>? _logger;
    private readonly CultureInfo _locale = CultureInfo.CurrentUICulture;
    private DialogueBundle? _bundle;
    private bool _bundleLoaded;
    private string? _activeServantId;

    public ServantFocusConnector(
        IArtPackageRepository repository,
        IFocusSessionService focus,
        AttachedPanelViewModel panel,
        PortraitController controller,
        EventFeedbackSelector selector,
        IPhase2Availability availability,
        ILogger<ServantFocusConnector>? logger = null)
    {
        _repository = repository;
        _focus = focus;
        _panel = panel;
        _controller = controller;
        _selector = selector;
        _availability = availability;
        _logger = logger;
        _focus.SnapshotChanged += OnFocusChanged;
    }

    /// <summary>Resolves the stable servant ID from the resolved pack and activates the portrait.</summary>
    public async Task ActivateAsync(PortraitSelection selection, CancellationToken cancellationToken)
    {
        await _controller.ActivateAsync(selection, cancellationToken).ConfigureAwait(false);
        try
        {
            var servants = await _repository.ListServantsAsync(cancellationToken).ConfigureAwait(false);
            var match = servants.FirstOrDefault(servant =>
                servant.Appearances.Any(appearance =>
                    appearance.AppearanceId == selection.AppearanceId
                    && appearance.PackageVersion == selection.PackageVersion))
                ?? servants.FirstOrDefault(servant => servant.PackageId == selection.PackageId);
            _activeServantId = match?.ServantId ?? selection.PackageId;
            _panel.DispatcherInvoke(() => _panel.SetActiveServant(_activeServantId));
        }
        catch (Exception error)
        {
            // Identity is best-effort: fall back to the package ID and keep going.
            _logger?.LogWarning(error, "Servant identity resolution failed; using package id.");
            _activeServantId = selection.PackageId;
            _panel.DispatcherInvoke(() => _panel.SetActiveServant(_activeServantId));
        }
    }

    private void OnFocusChanged(object? sender, EventArgs e)
    {
        if (!_availability.IsAvailable)
        {
            return;
        }

        try
        {
            EnsureBundle();
            var session = _focus.Current;
            if (session.Status is FocusStatus.Idle)
            {
                return;
            }

            var runtimeEvent = new RuntimeEvent(
                $"feedback-{session.SessionId}",
                session.SessionId,
                SessionEventType(session),
                session.UpdatedAtUtc,
                session.CurrentCycle,
                session.Phase,
                _activeServantId ?? session.ServantId,
                session.PhaseElapsedSeconds,
                EffectiveSeconds: 0,
                Priority: 0);
            var result = _selector.Select(runtimeEvent, _bundle, _locale);
            _panel.DispatcherInvoke(() => _panel.AddDialogue(result.Text));
            _controller.SetExpression(result.Expression);
        }
        catch (Exception error)
        {
            _logger?.LogWarning(error, "Event feedback selection failed; staying neutral.");
        }
    }

    private static string SessionEventType(FocusSession session) => session.Status switch
    {
        FocusStatus.Focusing => RuntimeEventType.FocusStarted,
        FocusStatus.Breaking => RuntimeEventType.FocusCompleted,
        FocusStatus.PausedFocus or FocusStatus.PausedBreak => RuntimeEventType.FocusStopped,
        FocusStatus.Completed => RuntimeEventType.CycleCompleted,
        _ => RuntimeEventType.FocusStarted,
    };

    private void EnsureBundle()
    {
        if (_bundleLoaded)
        {
            return;
        }

        _bundleLoaded = true;
        try
        {
            var location = _controller.CurrentState?.Selection;
            if (location is not null)
            {
                var servants = _repository.ListServantsAsync(CancellationToken.None).GetAwaiter().GetResult();
                var appearance = servants.SelectMany(servant => servant.Appearances)
                    .FirstOrDefault(candidate => candidate.AppearanceId == location.AppearanceId);
                var packRoot = FindPackRoot(appearance?.AppearanceRoot);
                if (packRoot is not null)
                {
                    _bundle = DialogueManifestReader.ReadOptional(packRoot);
                }
            }
        }
        catch (Exception error)
        {
            _logger?.LogWarning(error, "Dialogue bundle load failed; using neutral fallback.");
        }
    }

    private static string? FindPackRoot(string? appearanceRoot)
    {
        if (string.IsNullOrEmpty(appearanceRoot) || !Directory.Exists(appearanceRoot))
        {
            return null;
        }

        // Walk up from the appearance root looking for a dialogue/ directory, bounded.
        var current = appearanceRoot;
        for (var depth = 0; depth < 4 && current is not null; depth++)
        {
            if (Directory.Exists(Path.Combine(current, "dialogue")))
            {
                return current;
            }

            current = Path.GetDirectoryName(current);
        }

        return null;
    }
}

internal static class DispatcherExtensions
{
    public static void DispatcherInvoke(this AttachedPanelViewModel _, Action action)
    {
        if (System.Windows.Application.Current?.Dispatcher is { } dispatcher)
        {
            dispatcher.BeginInvoke(action);
        }
        else
        {
            action();
        }
    }
}
