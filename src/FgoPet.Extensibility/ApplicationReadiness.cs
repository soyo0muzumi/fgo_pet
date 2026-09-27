namespace FgoPet.Extensibility;

public sealed record ApplicationReadiness(bool StorageAvailable);

/// <summary>Optional owner hook after storage initialization. Repeated shell activations do not repeat it.</summary>
public interface IApplicationReadyObserver
{
    ValueTask OnApplicationReadyAsync(ApplicationReadiness readiness, CancellationToken cancellationToken);
}
