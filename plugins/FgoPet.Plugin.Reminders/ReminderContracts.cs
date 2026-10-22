using FgoPet.Extensibility;

namespace FgoPet.Plugin.Reminders;

public enum ReminderStatus { Pending, Dispatching, Submitted, Failed, Unknown, Cancelled, Expired }
public sealed record Reminder(string Id, ToolScope Scope, string Message, DateTimeOffset DueAtUtc,
    string TimeZoneId, DateTimeOffset RequestedTime, ReminderStatus Status, long Revision,
    DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, string? ErrorCode = null);
public sealed record ReminderDraft(string Message, DateTimeOffset DueAt, string TimeZoneId);
public sealed record ReminderDiagnostic(DateTimeOffset Timestamp, string Correlation, string Stage, string? ErrorCode = null);
public enum ReminderNotificationOutcome { Submitted, Unavailable, NotAttempted }
/// <summary>Submitted means the notification API call returned, not that the system displayed it or the user saw it.</summary>
public interface IReminderNotifier
{
    ValueTask<ReminderNotificationOutcome> NotifyAsync(Reminder reminder, CancellationToken token);
}
public sealed class ReminderException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
