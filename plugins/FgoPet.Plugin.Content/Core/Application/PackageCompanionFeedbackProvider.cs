using System.Globalization;
using FgoPet.App.Feedback;
using FgoPet.Core.Events;
using FgoPet.Core.Focus;
using FgoPet.Core.Packs;
using FgoPet.Core.Portraits;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Packs;

namespace FgoPet.Plugin.Content;

/// <summary>Loads feedback for the exact current package/version/appearance, without UI or renderer dependencies.</summary>
public sealed class PackageCompanionFeedbackProvider(IArtPackageRepository repository) : ICompanionFeedbackProvider
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly EventFeedbackSelector _selector = new();
    private readonly CultureInfo _locale = CultureInfo.CurrentUICulture;
    private CompanionContentScope? _loadedScope;
    private DialogueBundle? _bundle;

    public async Task<CompanionFeedback?> GetFeedbackAsync(CompanionSignal signal, CompanionContentScope scope, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_loadedScope != scope)
            {
                _loadedScope = null;
                _bundle = null;
                var location = await repository.GetAppearanceAsync(new(scope.PackageId, scope.AppearanceId, scope.PackageVersion), cancellationToken)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                var root = FindPackRoot(location?.AppearanceRoot);
                if (root is not null)
                    _bundle = await Task.Run(() => DialogueManifestReader.ReadOptional(root), cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                _loadedScope = scope;
            }
            var type = signal.Kind switch
            {
                CompanionSignalKind.ActivityStarted => RuntimeEventType.FocusStarted,
                CompanionSignalKind.ActivityPaused or CompanionSignalKind.ActivityStopped => RuntimeEventType.FocusStopped,
                CompanionSignalKind.ActivityCompleted => RuntimeEventType.CycleCompleted,
                _ => signal.Phase == CompanionActivityPhase.Work ? RuntimeEventType.FocusCompleted : RuntimeEventType.CycleCompleted
            };
            var fact = new RuntimeEvent($"feedback-{signal.ActivityId}", signal.ActivityId, type, signal.OccurredAtUtc,
                signal.Round, signal.Phase == CompanionActivityPhase.Work ? FocusPhase.Focus : FocusPhase.Break,
                scope.RoleId, signal.ElapsedSeconds, 0, 0);
            var result = _selector.Select(fact, _bundle, _locale);
            return new(result.Text, result.Expression);
        }
        finally { _gate.Release(); }
    }
    private static string? FindPackRoot(string? appearanceRoot)
    {
        if (string.IsNullOrEmpty(appearanceRoot) || !Directory.Exists(appearanceRoot)) return null;
        var current = appearanceRoot;
        for (var depth = 0; depth < 4 && current is not null; depth++)
        {
            if (Directory.Exists(Path.Combine(current, "dialogue"))) return current;
            current = Path.GetDirectoryName(current);
        }
        return null;
    }
}
