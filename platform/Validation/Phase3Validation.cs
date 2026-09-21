// Stage 2 / R5: sunk out of modules/dialogue/Contracts/ConversationContracts.cs.
// Domain-neutral validation helper shared by settings, packs, dialogue and memory.
// Leaving it inside the dialogue contract file made memory and character depend on
// dialogue, which closes a cycle once those directories become separate assemblies.
// Namespace moved FgoPet.Core.Dialogue -> FgoPet.Core.Validation to match its role,
// and the type is now public because consumers live in other assemblies after the split.
namespace FgoPet.Core.Validation;

public static class Phase3Validation
{
    public static string Id(string value, string parameterName, int maxLength = 128)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim();
        if (normalized.Length > maxLength)
        {
            throw new ArgumentException($"{parameterName} must be at most {maxLength} characters.", parameterName);
        }

        return normalized;
    }

    public static string Text(string value, string parameterName, int maxLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim();
        if (normalized.Length > maxLength)
        {
            throw new ArgumentException($"{parameterName} must be at most {maxLength} characters.", parameterName);
        }

        return normalized;
    }

    public static string OptionalText(string? value, string parameterName, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return Text(value, parameterName, maxLength);
    }

    /// <summary>
    /// Validates a streaming fragment. Fragments are concatenated verbatim, so a
    /// leading or trailing space is part of the text and must never be trimmed;
    /// trimming here runs English words together across deltas.
    /// </summary>
    public static string Fragment(string? value, string parameterName, int maxLength)
    {
        var fragment = value ?? string.Empty;
        if (fragment.Length > maxLength)
        {
            throw new ArgumentException($"{parameterName} must be at most {maxLength} characters.", parameterName);
        }

        return fragment;
    }
}
