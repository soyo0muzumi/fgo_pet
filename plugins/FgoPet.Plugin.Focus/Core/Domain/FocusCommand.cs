namespace FgoPet.Core.Focus;

/// <summary>Allowed user/time commands. One command triggers at most one transition.</summary>
public abstract record FocusCommand
{
    public sealed record Start(FocusPreset Preset, string ServantId) : FocusCommand;

    public sealed record Pause : FocusCommand;

    public sealed record Resume : FocusCommand;

    public sealed record Stop : FocusCommand;

    /// <summary>Elapsed whole seconds since the last consumed timestamp.</summary>
    public sealed record Elapsed(int Seconds) : FocusCommand;

    public sealed record Acknowledge : FocusCommand;
}
