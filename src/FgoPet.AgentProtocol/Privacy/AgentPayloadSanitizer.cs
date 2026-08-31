using System.Text.RegularExpressions;
using FgoPet.AgentProtocol.Messages;

namespace FgoPet.AgentProtocol.Privacy;

public static partial class AgentPayloadSanitizer
{
    public static AgentEventMessage Sanitize(AgentEventMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        EnsureSafeText(message.SourceType, nameof(message.SourceType));
        EnsureSafeText(message.SourceInstance, nameof(message.SourceInstance));
        EnsureSafeText(message.TaskId, nameof(message.TaskId));
        EnsureSafeText(message.TodoId, nameof(message.TodoId));
        EnsureSafeText(message.DispatchRequestId, nameof(message.DispatchRequestId));
        foreach (var coveredTaskKey in message.CoveredTaskKeys ?? Array.Empty<string>())
        {
            EnsureSafeText(coveredTaskKey, nameof(message.CoveredTaskKeys));
        }

        if (message.IsPrivate)
        {
            return message with { Title = null, Summary = null };
        }

        EnsureSafeText(message.Title, nameof(message.Title));
        EnsureSafeText(message.Summary, nameof(message.Summary));
        return message;
    }

    public static string SanitizeText(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        EnsureSafeText(value, fieldName);
        return value.Trim();
    }

    public static bool ContainsForbiddenText(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && (AbsolutePathPattern().IsMatch(value) || CredentialPattern().IsMatch(value));

    private static void EnsureSafeText(string? value, string fieldName)
    {
        if (ContainsForbiddenText(value))
        {
            throw new AgentProtocolValidationException($"{fieldName} contains a local path or credential-like value.");
        }
    }

    [GeneratedRegex(@"(?:[A-Za-z]:[\\/]|\\\\|/(?:Users|home|private|var|tmp)/)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AbsolutePathPattern();

    [GeneratedRegex(@"(?:sk-[A-Za-z0-9_-]{8,}|AKIA[0-9A-Z]{12,}|(?:bearer|token|password|secret)\s*[:=]\s*\S+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CredentialPattern();
}
