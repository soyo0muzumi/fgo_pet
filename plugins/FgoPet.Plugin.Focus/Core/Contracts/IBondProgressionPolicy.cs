namespace FgoPet.Core.Bond;

/// <summary>Replaceable internal progression contract; the default curve is versioned.</summary>
public interface IBondProgressionPolicy
{
    string Version { get; }

    int MaxLevel { get; }

    BondProgress Evaluate(long lifetimeFocusSeconds, int achievedLevel);
}
