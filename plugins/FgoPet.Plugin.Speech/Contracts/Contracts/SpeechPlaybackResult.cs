using FgoPet.Core.Speech;

// Preserve the existing source namespace while moving this value contract out of
// the Desktop implementation assembly. Deploy a complete rebuilt application.
namespace FgoPet.App.Speech;

public sealed record SpeechPlaybackResult(
    bool Completed,
    SpeechPlaybackState State,
    string? SafeError = null);
