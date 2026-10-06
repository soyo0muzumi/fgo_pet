using System.Collections.Immutable;
using FgoPet.App.Dialogue;
using FgoPet.Extensibility;
using FgoPet.Kernel.Agent;

namespace FgoPet.Core.Dialogue;

/// <summary>Only validated semantic content may be accepted for durable conversation delivery.</summary>
public sealed record AcceptedAgentFinal(AgentRunIdentity Identity, string DeliveryId, ProtectedQueryReference Source,
    ContentContextKey ContentContext, ValidatedChatOutput Output, DateTimeOffset AcceptedAt,
    ImmutableArray<ResolvedUserInput> Clarifications);
public sealed record AgentFinalDelivery(AcceptedAgentFinal Accepted, ChatMessage Assistant);

/// <summary>Dialogue owns protected acceptance, atomic semantic publication and at-most-once observer claiming.</summary>
public interface IAgentFinalDeliveryStore
{
    bool TryAccept(AcceptedAgentFinal accepted);
    AgentFinalDelivery? Publish(ToolScope scope, string deliveryId);
    IReadOnlyList<AcceptedAgentFinal> ReadPending(int limit = 100);
    bool TryClaimObservers(ToolScope scope, string deliveryId);
}
