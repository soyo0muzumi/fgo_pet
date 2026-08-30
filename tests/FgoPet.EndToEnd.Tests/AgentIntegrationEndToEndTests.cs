using FgoPet.AgentProtocol;
using FgoPet.AgentProtocol.Messages;
using FgoPet.AgentRelay.Registration;
using FgoPet.AgentRelay.Routing;
using FgoPet.AgentRelay.Storage;
using FgoPet.CodexAdapter.Hooks;
using FgoPet.CodexAdapter.Mcp;
using FgoPet.CodexAdapter.Relay;
using FgoPet.Core.Agents;
using FgoPet.Infrastructure.Agents;
using Xunit;

namespace FgoPet.EndToEnd.Tests;

public sealed class AgentIntegrationEndToEndTests
{
    [Fact]
    public async Task Confirmed_mcp_completion_travels_through_relay_and_reaches_terminal_projection()
    {
        var fixture = CreateFixture();
        var relay = new ForwardingRelay(fixture.Router, fixture.Grant.Credential);
        var server = new CodexMcpServer(relay, "codex", fixture.Grant.SourceInstance, "task-1");

        var response = await server.HandleAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"report_task_completed\",\"arguments\":{\"user_confirmed\":true,\"summary\":\"Delivered\"}}}");

        Assert.Contains("ok", response, StringComparison.Ordinal);
        Assert.Equal(RelayRouteResult.Queued, relay.LastRoute!.Result);

        var wire = Assert.Single(fixture.Router.DrainInbound()).DeserializePayload<AgentEventMessage>();
        var projector = new AgentEventProjector();
        var result = projector.Apply(new AgentEvent(
            wire.SourceType,
            wire.SourceInstance,
            wire.TaskId,
            wire.Sequence,
            AgentEventType.TaskCompleted,
            wire.OccurredAt,
            summary: wire.Summary));

        Assert.Equal(AgentProjectionApplyResult.Applied, result);
        var projection = Assert.Single(projector.Current);
        Assert.Equal(AgentExecutionStatus.Completed, projection.Status);
        Assert.Equal("Delivered", projection.Summary);
    }

    [Fact]
    public void Hook_events_are_idempotent_at_the_relay_boundary_and_private_payloads_are_redacted()
    {
        var fixture = CreateFixture();
        var observation = new CodexHookObservation("task-2", 3, CodexHookKind.Started, "working");
        var mapped = CodexHookMapper.Map(observation, "codex", fixture.Grant.SourceInstance);
        var first = ProtocolEnvelope.Create("hook-1", "agent_event", mapped, mapped.OccurredAt);
        var replay = ProtocolEnvelope.Create("hook-2", "agent_event", mapped, mapped.OccurredAt.AddMinutes(1));

        Assert.Equal(RelayRouteResult.Queued, fixture.Router.RouteAdapterEvent(fixture.Grant.Credential, first, first.SentAt).Result);
        Assert.Equal(RelayRouteResult.AlreadyApplied, fixture.Router.RouteAdapterEvent(fixture.Grant.Credential, replay, replay.SentAt).Result);
        Assert.Single(fixture.Router.DrainInbound());

        var privateMessage = new AgentEventMessage(
            "codex",
            fixture.Grant.SourceInstance,
            "task-2",
            4,
            "attention_required",
            DateTimeOffset.UtcNow,
            Title: "C:\\Users\\private\\secret.txt",
            Summary: "token=hidden",
            IsPrivate: true);
        var privateEnvelope = ProtocolEnvelope.Create("private-1", "agent_event", privateMessage, privateMessage.OccurredAt);

        Assert.Equal(RelayRouteResult.Queued, fixture.Router.RouteAdapterEvent(fixture.Grant.Credential, privateEnvelope, privateEnvelope.SentAt).Result);
        var redacted = Assert.Single(fixture.Router.DrainInbound()).DeserializePayload<AgentEventMessage>();
        Assert.Null(redacted.Title);
        Assert.Null(redacted.Summary);
    }

    [Fact]
    public void Relay_dispatch_is_offline_safe_and_repeated_request_is_stable_when_adapter_returns()
    {
        var fixture = CreateFixture();
        var request = new DispatchTaskRequest("dispatch-1", "todo-1", "Ship it", null, "normal", null, "opaque-project");
        fixture.Router.SetAllowedTargets("codex", new[] { "opaque-project" });

        var offline = fixture.Router.RouteDispatch(fixture.Grant.Credential, request, DateTimeOffset.UtcNow);
        Assert.Equal(RelayRouteResult.Offline, offline.Result);

        fixture.Router.SetAdapterOnline("codex", fixture.Grant.SourceInstance, true);
        var accepted = fixture.Router.RouteDispatch(fixture.Grant.Credential, request, DateTimeOffset.UtcNow);
        var duplicate = fixture.Router.RouteDispatch(fixture.Grant.Credential, request, DateTimeOffset.UtcNow.AddSeconds(1));

        Assert.Equal(RelayRouteResult.Accepted, accepted.Result);
        Assert.Equal(RelayRouteResult.AlreadyApplied, duplicate.Result);
        Assert.Equal(accepted.DispatchRequestId, duplicate.DispatchRequestId);
    }

    private static Fixture CreateFixture()
    {
        var store = new RelayStore();
        var registration = new RegistrationService(store);
        var router = new RelayRouter(store, registration);
        var at = DateTimeOffset.Parse("2026-08-30T08:00:00Z");
        var pending = registration.Request(new AdapterRegistrationRequest("codex", "Codex", "1.0"), at);
        var grant = registration.Approve(pending.RequestId, at.AddSeconds(1));
        return new Fixture(router, grant);
    }

    private sealed record Fixture(RelayRouter Router, RegistrationGrant Grant);

    private sealed class ForwardingRelay : ICodexRelaySession
    {
        private readonly RelayRouter _router;
        private readonly string _credential;

        public ForwardingRelay(RelayRouter router, string credential)
        {
            _router = router;
            _credential = credential;
        }

        public RelayRouteReceipt? LastRoute { get; private set; }

        public Task SendEventAsync(AgentEventMessage message, CancellationToken cancellationToken = default)
        {
            var envelope = ProtocolEnvelope.Create(
                $"event-{message.SourceType}-{message.SourceInstance}-{message.TaskId}-{message.Sequence}",
                "agent_event",
                message,
                message.OccurredAt);
            LastRoute = _router.RouteAdapterEvent(_credential, envelope, message.OccurredAt);
            return Task.CompletedTask;
        }
    }
}
