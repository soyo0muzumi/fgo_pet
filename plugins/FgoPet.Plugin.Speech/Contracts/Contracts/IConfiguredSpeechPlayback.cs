using FgoPet.App.Speech;

namespace FgoPet.Core.Speech;

/// <summary>
/// Reads completed message text using Speech's saved configuration. Speech owns
/// provider selection, auto-read/DND policy, filtering, cancellation and audio cleanup.
/// This port exposes neither raw synthesis nor settings, credentials or audio devices.
/// The composition root retains ownership of the implementation's lifetime.
/// </summary>
public interface IConfiguredSpeechPlayback
{
    SpeechPlaybackState State { get; }

    /// <summary>
    /// State may change on a worker thread. UI consumers must marshal notifications
    /// to their own dispatcher and discard work for obsolete requests or lifetimes.
    /// </summary>
    event EventHandler? StateChanged;

    Task<SpeechPlaybackResult> PlayConfiguredAsync(
        string? text,
        bool autoRead = false,
        CancellationToken cancellationToken = default);

    void Stop();
}
