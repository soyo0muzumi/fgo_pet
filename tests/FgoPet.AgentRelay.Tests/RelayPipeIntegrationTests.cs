using FgoPet.AgentProtocol;
using FgoPet.AgentProtocol.Messages;
using FgoPet.AgentRelay.Pipes;
using FgoPet.AgentRelay.Registration;
using FgoPet.AgentRelay.Routing;
using FgoPet.AgentRelay.Storage;
using Xunit;

namespace FgoPet.AgentRelay.Tests;

public sealed class RelayPipeIntegrationTests
{
    [Fact]
    public async Task Adapter_pipe_round_trip_uses_the_relay_router_without_tcp()
    {
        var store = new RelayStore();
        var registration = new RegistrationService(store);
        var router = new RelayRouter(store, registration);
        var pending = registration.Request(new AdapterRegistrationRequest("codex", "Codex", "1.0"), DateTimeOffset.UtcNow);
        var grant = registration.Approve(pending.RequestId, DateTimeOffset.UtcNow);
        var server = new AdapterPipeServer(router, "unused-test-pipe", grant.Credential);

        var envelope = ProtocolEnvelope.Create("event-1", "agent_event", new AgentEventMessage(
            "codex", grant.SourceInstance, "task-1", 1, "task_started", DateTimeOffset.UtcNow));
        var result = await server.ProcessLineAsync(envelope.ToJson());

        Assert.Contains("queued", result, StringComparison.OrdinalIgnoreCase);
        Assert.Single(router.DrainInbound());
    }
}
