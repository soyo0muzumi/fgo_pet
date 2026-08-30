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

    [Fact]
    public async Task App_pipe_rejects_a_dispatch_payload_wrapped_in_the_wrong_message_type()
    {
        var store = new RelayStore();
        var registration = new RegistrationService(store);
        var router = new RelayRouter(store, registration);
        var at = DateTimeOffset.Parse("2026-08-30T08:00:00Z");
        var pending = registration.Request(new AdapterRegistrationRequest("codex", "Codex", "1.0"), at);
        var grant = registration.Approve(pending.RequestId, at.AddSeconds(1));
        router.SetAdapterOnline("codex", grant.SourceInstance, true);
        var server = new AppPipeServer(router, "unused-test-pipe", grant.Credential);
        var request = new DispatchTaskRequest("dispatch-1", "todo-1", "Ship it", null, "normal", null, "opaque-project");
        var wrongEnvelope = ProtocolEnvelope.Create("wrong-type", "agent_event", request, at);

        await Assert.ThrowsAsync<AgentProtocolValidationException>(() => server.ProcessLineAsync(wrongEnvelope.ToJson()));
    }
}
