using FgoPet.AgentProtocol;
using FgoPet.AgentProtocol.Messages;
using FgoPet.AgentRelay.Registration;
using FgoPet.AgentRelay.Routing;
using FgoPet.AgentRelay.Storage;
using Xunit;

namespace FgoPet.AgentRelay.Tests;

public sealed class RelayRouterTests
{
    [Fact]
    public void Events_queue_only_while_app_is_offline_and_are_dropped_when_disabled()
    {
        var store = new RelayStore();
        var registration = new RegistrationService(store);
        var router = new RelayRouter(store, registration);
        var at = DateTimeOffset.Parse("2026-08-30T08:00:00Z");
        var grant = Approve(registration, at);
        var envelope = ProtocolEnvelope.Create("event-1", "agent_event", new AgentEventMessage(
            "codex", grant.SourceInstance, "task-1", 1, "task_started", at));

        var queued = router.RouteAdapterEvent(grant.Credential, envelope, at);
        Assert.Equal(RelayRouteResult.Queued, queued.Result);
        Assert.Equal(1, router.PendingInboundCount);

        router.SetConnectionEnabled(false);
        Assert.Equal(0, router.PendingInboundCount);
        var dropped = router.RouteAdapterEvent(grant.Credential, envelope with { MessageId = "event-2" }, at.AddMinutes(1));
        Assert.Equal(RelayRouteResult.Disabled, dropped.Result);
        Assert.Empty(router.DrainInbound());
    }

    [Fact]
    public void Dispatch_is_online_only_and_duplicate_request_returns_original_result()
    {
        var store = new RelayStore();
        var registration = new RegistrationService(store);
        var router = new RelayRouter(store, registration);
        var at = DateTimeOffset.Parse("2026-08-30T08:00:00Z");
        var grant = Approve(registration, at);
        router.SetAdapterOnline("codex", grant.SourceInstance, true);
        var request = new DispatchTaskRequest("dispatch-1", "todo-1", "Ship it", null, "normal", null, "opaque-project");

        var first = router.RouteDispatch(grant.Credential, request, at);
        var second = router.RouteDispatch(grant.Credential, request, at.AddMinutes(1));

        Assert.Equal(RelayRouteResult.Accepted, first.Result);
        Assert.Equal(RelayRouteResult.AlreadyApplied, second.Result);
        Assert.Equal(first.DispatchRequestId, second.DispatchRequestId);
    }

    [Fact]
    public void Event_deduplication_uses_source_task_sequence_not_envelope_id()
    {
        var store = new RelayStore();
        var registration = new RegistrationService(store);
        var router = new RelayRouter(store, registration);
        var at = DateTimeOffset.Parse("2026-08-30T08:00:00Z");
        var grant = Approve(registration, at);
        var message = new AgentEventMessage("codex", grant.SourceInstance, "task-1", 1, "task_started", at);
        var first = ProtocolEnvelope.Create("event-1", "agent_event", message, at);
        var replay = ProtocolEnvelope.Create("event-2", "agent_event", message, at.AddMinutes(1));

        Assert.Equal(RelayRouteResult.Queued, router.RouteAdapterEvent(grant.Credential, first, at).Result);
        Assert.Equal(RelayRouteResult.AlreadyApplied, router.RouteAdapterEvent(grant.Credential, replay, at).Result);
        Assert.Single(router.DrainInbound());
    }

    [Fact]
    public void Private_event_is_redacted_before_it_enters_the_inbound_queue()
    {
        var store = new RelayStore();
        var registration = new RegistrationService(store);
        var router = new RelayRouter(store, registration);
        var at = DateTimeOffset.Parse("2026-08-30T08:00:00Z");
        var grant = Approve(registration, at);
        var envelope = ProtocolEnvelope.Create("event-1", "agent_event", new AgentEventMessage(
            "codex", grant.SourceInstance, "task-1", 1, "attention_required", at,
            "C:\\\\Users\\\\secret.txt", "sk-proj-1234567890", IsPrivate: true));

        Assert.Equal(RelayRouteResult.Queued, router.RouteAdapterEvent(grant.Credential, envelope, at).Result);
        var queued = Assert.Single(router.DrainInbound()).DeserializePayload<AgentEventMessage>();
        Assert.Null(queued.Title);
        Assert.Null(queued.Summary);
    }

    private static RegistrationGrant Approve(RegistrationService service, DateTimeOffset at)
    {
        var pending = service.Request(new AdapterRegistrationRequest("codex", "Codex", "1.0"), at);
        return service.Approve(pending.RequestId, at.AddSeconds(1));
    }
}
