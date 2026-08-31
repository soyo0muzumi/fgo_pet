namespace FgoPet.Core.Portraits;

public enum ExpressionSemantic
{
    Neutral,
    Happy,
    Excited,
    Shy,
    Concerned,
    Sad,
    Surprised,
    Angry,
    WantsToTalk,
}

/// <summary>Canonical snake_case keys for the eight core expression semantics.</summary>
public static class ExpressionSemanticKeys
{
    public const string Neutral = "neutral";
    public const string Happy = "happy";
    public const string Excited = "excited";
    public const string Shy = "shy";
    public const string Concerned = "concerned";
    public const string Sad = "sad";
    public const string Surprised = "surprised";
    public const string Angry = "angry";
    public const string WantsToTalk = "wants_to_talk";

    public static readonly IReadOnlyList<string> Core = new[]
    {
        Neutral, Happy, Excited, Shy, Concerned, Sad, Surprised, Angry,
    };

    public static string Key(ExpressionSemantic semantic) => semantic switch
    {
        ExpressionSemantic.Neutral => Neutral,
        ExpressionSemantic.Happy => Happy,
        ExpressionSemantic.Excited => Excited,
        ExpressionSemantic.Shy => Shy,
        ExpressionSemantic.Concerned => Concerned,
        ExpressionSemantic.Sad => Sad,
        ExpressionSemantic.Surprised => Surprised,
        ExpressionSemantic.Angry => Angry,
        ExpressionSemantic.WantsToTalk => WantsToTalk,
        _ => throw new ArgumentOutOfRangeException(nameof(semantic), semantic, null),
    };

    public static bool TryParseKey(string? key, out ExpressionSemantic semantic)
    {
        switch (key)
        {
            case Neutral: semantic = ExpressionSemantic.Neutral; return true;
            case Happy: semantic = ExpressionSemantic.Happy; return true;
            case Excited: semantic = ExpressionSemantic.Excited; return true;
            case Shy: semantic = ExpressionSemantic.Shy; return true;
            case Concerned: semantic = ExpressionSemantic.Concerned; return true;
            case Sad: semantic = ExpressionSemantic.Sad; return true;
            case Surprised: semantic = ExpressionSemantic.Surprised; return true;
            case Angry: semantic = ExpressionSemantic.Angry; return true;
            case WantsToTalk: semantic = ExpressionSemantic.WantsToTalk; return true;
            default: semantic = default; return false;
        }
    }
}
