namespace FgoPet.Core.Dialogue;

public enum ExpressionMode { Off, LegacyV0, BasePolicy, Full }

/// <summary>Ephemeral host intent. Expression preserves this object; it cannot form a stance.</summary>
public sealed record ResponseIntent
{
    public ResponseIntent(string act, string stance, string focus, string tone, string depth, string followUp,
        IReadOnlyList<string> avoid)
    {
        Act = Token(act); Stance = Token(stance); Focus = Token(focus); Tone = Token(tone);
        Depth = Token(depth); FollowUp = Token(followUp);
        ArgumentNullException.ThrowIfNull(avoid);
        if (avoid.Count > 12) throw new ArgumentOutOfRangeException(nameof(avoid));
        Avoid = Array.AsReadOnly(avoid.Select(Token).Distinct(StringComparer.Ordinal).ToArray());
    }
    public string Act { get; }
    public string Stance { get; }
    public string Focus { get; }
    public string Tone { get; }
    public string Depth { get; }
    public string FollowUp { get; }
    public IReadOnlyList<string> Avoid { get; }
    private static string Token(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64 || value.Any(c => !(c is >= 'a' and <= 'z' or '_')))
            throw new ArgumentException("Expression intent fields must be bounded symbolic tokens.");
        return value;
    }
}
