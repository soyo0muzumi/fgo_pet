namespace FgoPet.Plugin.Qq.Transport;

/// <summary>The secret is resolved from protected storage; it is never part of channel settings.</summary>
public sealed record QqBotConnection(string AppId, string AppSecretCredentialTarget);

/// <summary>A validated C2C envelope. Unsupported media carries no body or attachment URL and never enters a model.</summary>
public sealed record QqC2cMessage(string MessageId, string UserOpenId, string Content, DateTimeOffset Timestamp,
    bool IsUnsupportedMedia = false);

public enum QqTransportState { Connecting, Connected, Reconnecting, Stopped, Faulted }

/// <summary>Contains only a local state and a fixed, content-free diagnostic code.</summary>
public sealed record QqTransportStatus(QqTransportState State, string? ErrorCode = null);

/// <summary>QQ_SEND_UNKNOWN means delivery cannot be established; the caller must not blindly replay it.</summary>
public sealed record QqSendResult(bool Succeeded, string? ErrorCode = null);

public interface IQqBotTransport : IAsyncDisposable
{
    /// <summary>
    /// Runs once, until cancelled or a terminal configuration/platform error occurs. The message callback
    /// must only admit to the host's bounded queue; model calls and user authorization belong to the host.
    /// Callbacks run off the UI thread and must not log message bodies or identifiers.
    /// </summary>
    Task RunAsync(QqBotConnection connection, Func<QqC2cMessage, CancellationToken, Task> onMessage,
        Action<QqTransportStatus> onStatus, CancellationToken cancellationToken);

    /// <summary>
    /// Replies only to a message received by this instance, within a conservative five-minute window.
    /// Up to 12,000 UTF-16 units are split without breaking surrogate pairs, using at most three replies.
    /// It never falls back to an unsolicited/proactive message or retries an uncertain send.
    /// </summary>
    Task<QqSendResult> ReplyTextAsync(QqC2cMessage message, string text, CancellationToken cancellationToken);
}
