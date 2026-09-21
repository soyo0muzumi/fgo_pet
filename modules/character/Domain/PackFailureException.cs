namespace FgoPet.Core.Packs;

/// <summary>
/// Thrown when a strict contract check fails. Carries the stable <see cref="PackFailure"/>
/// record so callers can map it to diagnostics without string matching.
/// </summary>
public sealed class PackFailureException : Exception
{
    public PackFailureException(PackFailure failure)
        : base(failure.ToString())
        => Failure = failure;

    public PackFailure Failure { get; }
}