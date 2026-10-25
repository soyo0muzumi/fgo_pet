namespace FgoPet.Core.Dialogue;

/// <summary>Trusted host admission at the actual transport handoff, after asynchronous preparation.
/// Implementations may charge durable quotas and retain a short permission lease through start().</summary>
public interface IChatDispatchGate
{
    T Dispatch<T>(ChatRequest request, ModelRouteKey route, Func<T> start, CancellationToken cancellationToken);
}
