using FgoPet.AgentProtocol;
using FgoPet.AgentProtocol.Messages;
using FgoPet.AgentRelay.Pipes;
using FgoPet.AgentRelay.Registration;
using FgoPet.AgentRelay.Routing;
using FgoPet.AgentRelay.Storage;
using System.Text.Json;
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

    [Fact]
    public async Task App_pipe_status_check_drains_sanitized_inbound_events_for_the_app()
    {
        var store = new RelayStore();
        var registration = new RegistrationService(store);
        var router = new RelayRouter(store, registration);
        var at = DateTimeOffset.Parse("2026-08-30T08:00:00Z");
        var pending = registration.Request(new AdapterRegistrationRequest("codex", "Codex", "1.0"), at);
        var grant = registration.Approve(pending.RequestId, at.AddSeconds(1));
        var adapter = new AdapterPipeServer(router, "unused-adapter", grant.Credential);
        var message = new AgentEventMessage("codex", grant.SourceInstance, "task-1", 1, "task_started", at, Summary: "safe");
        await adapter.ProcessLineAsync(ProtocolEnvelope.Create("event-1", "agent_event", message, at).ToJson());
        var app = new AppPipeServer(router, "unused-app", grant.Credential);

        var response = await app.ProcessLineAsync(ProtocolEnvelope.Create(
            "status-1", "status_check", new { include_events = true }, at).ToJson());
        using var document = JsonDocument.Parse(response);

        Assert.Equal("status", document.RootElement.GetProperty("result").GetString());
        Assert.Single(document.RootElement.GetProperty("events").EnumerateArray());
        Assert.Equal(0, router.PendingInboundCount);
    }

    [Fact]
    public async Task Adapter_status_check_polls_dispatches_queued_by_the_app_pipe()
    {
        var store = new RelayStore();
        var registration = new RegistrationService(store);
        var router = new RelayRouter(store, registration);
        var at = DateTimeOffset.Parse("2026-08-30T08:00:00Z");
        var pending = registration.Request(new AdapterRegistrationRequest("codex", "Codex", "1.0"), at);
        var grant = registration.Approve(pending.RequestId, at.AddSeconds(1));
        router.SetAdapterOnline("codex", grant.SourceInstance, true);
        router.SetAllowedTargets("codex", new[] { "opaque-project" });
        var app = new AppPipeServer(router, "unused-app", grant.Credential);
        var adapter = new AdapterPipeServer(router, "unused-adapter", grant.Credential);
        var request = new DispatchTaskRequest("dispatch-1", "todo-1", "Ship it", null, "normal", null, "opaque-project");

        await app.ProcessLineAsync(ProtocolEnvelope.Create("dispatch-1", "dispatch_task", request, at).ToJson());
        var response = await adapter.ProcessLineAsync(ProtocolEnvelope.Create(
            "poll-1", "status_check", new { include_dispatches = true }, at).ToJson());
        using var document = JsonDocument.Parse(response);
        var dispatch = ProtocolEnvelope.Parse(document.RootElement.GetProperty("dispatches")[0].GetString()!);

        Assert.Equal("dispatch_task", dispatch.MessageType);
        Assert.Equal(request, dispatch.DeserializePayload<DispatchTaskRequest>());
        Assert.Empty(router.DrainOutbound(grant.Credential, at));
    }

    [Fact]
    public async Task App_pipe_pushes_source_switch_and_allowlist_to_the_relay()
    {
        var store = new RelayStore();
        var registration = new RegistrationService(store);
        var router = new RelayRouter(store, registration);
        var at = DateTimeOffset.Parse("2026-08-30T08:00:00Z");
        var pending = registration.Request(new AdapterRegistrationRequest("codex", "Codex", "1.0"), at);
        var grant = registration.Approve(pending.RequestId, at.AddSeconds(1));
        router.SetAdapterOnline("codex", grant.SourceInstance, true);
        var app = new AppPipeServer(router, "unused-app", grant.Credential);

        await app.ProcessLineAsync(ProtocolEnvelope.Create(
            "settings-1",
            "status_check",
            new { source_type = "codex", source_enabled = false, allowed_targets = new[] { "opaque-project" } },
            at).ToJson());

        var request = new DispatchTaskRequest("dispatch-1", "todo-1", "Ship it", null, "normal", null, "opaque-project");
        Assert.Equal(RelayRouteResult.Disabled, router.RouteDispatch(grant.Credential, request, at).Result);

        await app.ProcessLineAsync(ProtocolEnvelope.Create(
            "settings-2",
            "status_check",
            new { source_type = "codex", source_enabled = true, allowed_targets = new[] { "opaque-project" } },
            at.AddMinutes(1)).ToJson());

        Assert.Equal(RelayRouteResult.Accepted, router.RouteDispatch(grant.Credential, request, at.AddMinutes(1)).Result);
    }
}
