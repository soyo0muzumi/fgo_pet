namespace FgoPet.Core.Packs;

/// <summary>
/// A frozen, dependency-free SemVer implementation used for package version identity
/// and ordering inside the repository. Precedence follows SemVer 2.0.0.
/// </summary>
public sealed record SemVersion(int Major, int Minor, int Patch, string? PreRelease = null) : IComparable<SemVersion>
{
    public static SemVersion Parse(string text) =>
        TryParse(text, out var version)
            ? version
            : throw new FormatException($"不是有效的 SemVer: '{text}'。");

    public static bool TryParse(string? text, out SemVersion version)
    {
        version = default!;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var core = text;
        var preRelease = (string?)null;
        var dash = text.IndexOf('-');
        if (dash >= 0)
        {
            core = text[..dash];
            preRelease = text[(dash + 1)..];
        }

        var parts = core.Split('.');
        if (parts.Length != 3
            || !int.TryParse(parts[0], out var major) || major < 0
            || !int.TryParse(parts[1], out var minor) || minor < 0
            || !int.TryParse(parts[2], out var patch) || patch < 0)
        {
            return false;
        }

        if (preRelease is not null && !IsValidPreRelease(preRelease))
        {
            return false;
        }

        version = new SemVersion(major, minor, patch, preRelease);
        return true;
    }

    public int CompareTo(SemVersion? other)
    {
        if (other is null) return 1;
        var byCore = (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));
        if (byCore != 0) return byCore;

        if (PreRelease is null && other.PreRelease is null) return 0;
        if (PreRelease is null) return 1; // release > pre-release
        if (other.PreRelease is null) return -1;
        return ComparePreRelease(PreRelease, other.PreRelease);
    }

    public override string ToString() => PreRelease is null
        ? $"{Major}.{Minor}.{Patch}"
        : $"{Major}.{Minor}.{Patch}-{PreRelease}";

    private static bool IsValidPreRelease(string value)
    {
        foreach (var identifier in value.Split('.'))
        {
            if (identifier.Length == 0) return false;
            var allDigits = identifier.All(char.IsAsciiDigit);
            if (allDigits && identifier.Length > 1 && identifier[0] == '0') return false;
            foreach (var character in identifier)
            {
                if (!(char.IsAsciiLetterOrDigit(character) || character == '-')) return false;
            }
        }
        return true;
    }

    private static int ComparePreRelease(string left, string right)
    {
        var leftIds = left.Split('.');
        var rightIds = right.Split('.');
        var count = Math.Max(leftIds.Length, rightIds.Length);
        for (var index = 0; index < count; index++)
        {
            if (index >= leftIds.Length) return -1; // shorter pre-release list has lower precedence
            if (index >= rightIds.Length) return 1;
            var comparison = CompareIdentifier(leftIds[index], rightIds[index]);
            if (comparison != 0) return comparison;
        }
        return 0;
    }

    private static int CompareIdentifier(string left, string right)
    {
        var leftNumeric = int.TryParse(left, out var leftValue);
        var rightNumeric = int.TryParse(right, out var rightValue);
        if (leftNumeric && rightNumeric) return leftValue.CompareTo(rightValue);
        if (leftNumeric) return -1; // numeric identifiers have lower precedence
        if (rightNumeric) return 1;
        return string.CompareOrdinal(left, right);
    }
}