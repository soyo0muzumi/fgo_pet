using System.Globalization;
using FgoPet.Core.Events;
using FgoPet.Core.Packs;
using FgoPet.Core.Portraits;

namespace FgoPet.App.Feedback;

/// <summary>Selected feedback: plain text plus an expression request.</summary>
public sealed record FeedbackResult(
    string Text,
    ExpressionSemantic Expression,
    string Locale,
    string? CandidateId);

/// <summary>
/// Package candidate selection with a bounded recent-dedupe history and a neutral
/// status-only fallback. Never throws: dialogue problems degrade to neutral text.
/// </summary>
public sealed class EventFeedbackSelector
{
    private const int RecentHistoryPerKey = 5;

    private readonly object _gate = new();
    private readonly Dictionary<(string PackageId, string EventType), Queue<string>> _recent = new();

    public FeedbackResult Select(RuntimeEvent runtimeEvent, DialogueBundle? bundle, CultureInfo appLocale)
    {
        if (bundle is null)
        {
            return new FeedbackResult(NeutralText(runtimeEvent.Type), ExpressionSemantic.Neutral, appLocale.Name, null);
        }

        var locale = ResolveLocale(bundle, appLocale);
        if (!bundle.Localizations.TryGetValue(locale, out var localization)
            || !localization.Events.TryGetValue(runtimeEvent.Type, out var candidates)
            || candidates.Count == 0)
        {
            return new FeedbackResult(NeutralText(runtimeEvent.Type), ExpressionSemantic.Neutral, locale, null);
        }

        var candidate = PickCandidate(runtimeEvent.SessionId, runtimeEvent.Type, candidates);
        return new FeedbackResult(
            candidate.Text,
            candidate.Expression ?? ExpressionSemantic.Neutral,
            locale,
            candidate.Id);
    }

    private static string ResolveLocale(DialogueBundle bundle, CultureInfo appLocale)
    {
        if (bundle.Localizations.ContainsKey(appLocale.Name))
        {
            return appLocale.Name;
        }

        if (bundle.Localizations.ContainsKey(appLocale.TwoLetterISOLanguageName))
        {
            return appLocale.TwoLetterISOLanguageName;
        }

        return bundle.DefaultLocale;
    }

    private DialogueCandidate PickCandidate(string packageKey, string eventType, IReadOnlyList<DialogueCandidate> candidates)
    {
        lock (_gate)
        {
            var key = (packageKey, eventType);
            if (!_recent.TryGetValue(key, out var history))
            {
                history = new Queue<string>();
                _recent[key] = history;
            }

            // Prefer candidates not shown recently; rotate deterministically otherwise.
            var fresh = candidates.Where(candidate => !history.Contains(candidate.Id)).ToArray();
            var pool = fresh.Length > 0 ? fresh : candidates.ToArray();
            var candidate = pool[history.Count % pool.Length];

            history.Enqueue(candidate.Id);
            while (history.Count > Math.Min(RecentHistoryPerKey, candidates.Count))
            {
                history.Dequeue();
            }

            return candidate;
        }
    }

    /// <summary>Status-only neutral strings: no servant name, no form of address.</summary>
    private static string NeutralText(string eventType) => eventType switch
    {
        RuntimeEventType.FocusStarted => "专注开始。",
        RuntimeEventType.FocusCompleted => "专注完成。",
        RuntimeEventType.FocusStopped => "专注已停止。",
        RuntimeEventType.CycleCompleted => "本轮循环完成。",
        RuntimeEventType.BondLevelUp => "羁绊等级提升。",
        _ => "状态已更新。",
    };

    /// <summary>Neutral expression request used by every fallback path.</summary>
    public const ExpressionSemantic NeutralExpression = ExpressionSemantic.Neutral;
}
