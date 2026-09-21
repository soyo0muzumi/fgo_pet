using FgoPet.Core.Portraits;

namespace FgoPet.Core.Packs;

/// <summary>
/// Safe package dialogue models. Text is plain, bounded (1-160 Unicode scalar
/// values), never interpreted as Markdown/URL/paths/templates/conditions. Only
/// the eight core expression semantics are accepted.
/// </summary>
public sealed record DialogueCandidate(
    string Id,
    string Text,
    int Weight = 100,
    ExpressionSemantic? Expression = null);

public sealed record DialogueLocalization(
    string Locale,
    IReadOnlyDictionary<string, IReadOnlyList<DialogueCandidate>> Events);

public sealed record DialogueBundle(
    string DefaultLocale,
    IReadOnlyDictionary<string, DialogueLocalization> Localizations);

public static class DialogueEventTypes
{
    public const string FocusStarted = "focus_started";
    public const string FocusCompleted = "focus_completed";
    public const string FocusStopped = "focus_stopped";
    public const string CycleCompleted = "cycle_completed";
    public const string BondLevelUp = "bond_level_up";
}
