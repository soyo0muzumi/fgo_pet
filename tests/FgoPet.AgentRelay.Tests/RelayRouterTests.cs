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
        router.TouchAdapterOnline(grant, at);
        router.SetAllowedTargets("codex", new[] { "opaque-project" });
        var request = new DispatchTaskRequest("dispatch-1", "todo-1", "Ship it", null, "normal", null, "opaque-project");

        var first = router.RouteDispatch(grant.Credential, request, at);
        var second = router.RouteDispatch(grant.Credential, request, at.AddMinutes(1));

        Assert.Equal(RelayRouteResult.Accepted, first.Result);
        Assert.Equal(RelayRouteResult.AlreadyApplied, second.Result);
        Assert.Equal(first.DispatchRequestId, second.DispatchRequestId);
        Assert.Equal(request.DispatchRequestId, first.TaskId);
        Assert.Equal(grant.SourceInstance, first.SourceInstance);
        var outbound = Assert.Single(router.DrainOutbound(grant.Credential, at.AddMinutes(1)));
        Assert.Equal(request, outbound.Request);
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

    [Fact]
    public void Relay_delivery_survives_restart_until_explicit_acknowledgement()
    {
        var state = new InMemoryRelayStateStore();
        var firstStore = new RelayStore(state);
        var firstRegistration = new RegistrationService(firstStore);
        var at = DateTimeOffset.Parse("2026-08-30T08:00:00Z");
        var grant = Approve(firstRegistration, at);
        var firstRouter = new RelayRouter(firstStore, firstRegistration);
        firstRouter.TouchAdapterOnline(grant, at);
        firstRouter.SetAllowedTargets("codex", new[] { "opaque-project" });
        var message = new AgentEventMessage("codex", grant.SourceInstance, "task-1", 1, "task_started", at);
        Assert.Equal(RelayRouteResult.Queued, firstRouter.RouteAdapterEvent(
            grant.Credential, ProtocolEnvelope.Create("event-1", "agent_event", message, at), at).Result);
        var request = new DispatchTaskRequest("dispatch-1", "todo-1", "Ship it", null, "normal", null, "opaque-project")
        {
            SourceType = "codex", SourceInstanceId = grant.SourceInstance,
        };
        Assert.Equal(RelayRouteResult.Accepted, firstRouter.RouteDispatch(grant.Credential, request, at).Result);

        var restartedStore = new RelayStore(state);
        var restartedRegistration = new RegistrationService(restartedStore);
        var restartedRouter = new RelayRouter(restartedStore, restartedRegistration);
        restartedRouter.TouchAdapterOnline(grant, at);
        Assert.Single(restartedRouter.DrainInbound(consume: false));
        Assert.Single(restartedRouter.DrainOutbound(grant, at, consume: false));

        Assert.Equal("acknowledged", restartedRouter.AcknowledgeInbound(new EventAcknowledgementRequest(
            "codex", grant.SourceInstance, new[] { new EventAcknowledgement("task-1", 1) }), at));
        Assert.Equal("acknowledged", restartedRouter.AcknowledgeDispatches(grant, new DispatchAcknowledgementRequest(
            "codex", grant.SourceInstance, new[] { request.DispatchRequestId }), at));
        var afterAck = new RelayStore(state);
        Assert.Empty(afterAck.DrainInbound(consume: false));
        Assert.Empty(afterAck.DrainOutbound("codex", grant.SourceInstance, consume: false));
    }

    [Fact]
    public void Clearing_pending_delivery_removes_receipts_so_retry_is_not_a_false_duplicate()
    {
        var store = new RelayStore();
        var registration = new RegistrationService(store);
        var router = new RelayRouter(store, registration);
        var at = DateTimeOffset.Parse("2026-08-30T08:00:00Z");
        var grant = Approve(registration, at);
        router.TouchAdapterOnline(grant, at);
        router.SetAllowedTargets("codex", new[] { "opaque-project" });
        var request = new DispatchTaskRequest("dispatch-1", "todo-1", "Ship it", null, "normal", null, "opaque-project");
        Assert.Equal(RelayRouteResult.Accepted, router.RouteDispatch(grant.Credential, request, at).Result);
        router.ClearPending();
        Assert.Equal(RelayRouteResult.Accepted, router.RouteDispatch(grant.Credential, request, at).Result);
    }

    [Fact]
    public void Outbound_capacity_returns_backpressure_without_dropping_the_new_request()
    {
        var store = new RelayStore();
        var registration = new RegistrationService(store);
        var router = new RelayRouter(store, registration);
        var at = DateTimeOffset.Parse("2026-08-30T08:00:00Z");
        var grant = Approve(registration, at);
        router.TouchAdapterOnline(grant, at);
        router.SetAllowedTargets("codex", new[] { "opaque-project" });
        for (var index = 0; index < RelayStore.MaxQueuedDispatches; index++)
        {
            var request = new DispatchTaskRequest($"dispatch-{index}", $"todo-{index}", "Ship it", null, "normal", null, "opaque-project");
            Assert.Equal(RelayRouteResult.Accepted, router.RouteDispatch(grant.Credential, request, at).Result);
        }

        var rejected = router.RouteDispatch(grant.Credential,
            new DispatchTaskRequest("dispatch-over-capacity", "todo-over-capacity", "Ship it", null, "normal", null, "opaque-project"), at);

        Assert.Equal(RelayRouteResult.Backpressure, rejected.Result);
        Assert.Equal(RelayStore.MaxQueuedDispatches, store.Snapshot.Outbound.Count);
        Assert.Null(store.GetDispatchReceipt("dispatch-over-capacity"));
    }

    [Fact]
    public void Acknowledged_events_release_queue_dedupe_keys_but_keep_sequence_watermark()
    {
        var store = new RelayStore();
        var registration = new RegistrationService(store);
        var router = new RelayRouter(store, registration);
        var at = DateTimeOffset.Parse("2026-08-30T08:00:00Z");
        var grant = Approve(registration, at);
        for (var sequence = 1; sequence <= RelayStore.MaxInboundEventKeys + 1; sequence++)
        {
            var message = new AgentEventMessage("codex", grant.SourceInstance, "task-1", sequence, "task_updated", at);
            Assert.Equal(RelayRouteResult.Queued, router.RouteAdapterEvent(
                grant.Credential, ProtocolEnvelope.Create($"event-{sequence}", "agent_event", message, at), at).Result);
            Assert.Equal("acknowledged", router.AcknowledgeInbound(new EventAcknowledgementRequest(
                "codex", grant.SourceInstance, new[] { new EventAcknowledgement("task-1", sequence) }), at));
        }

        Assert.Empty(store.Snapshot.InboundEventKeys);
        Assert.Single(store.Snapshot.InboundEventWatermarks);
        Assert.Equal(RelayStore.MaxInboundEventKeys + 1, store.Snapshot.InboundEventWatermarks[0].Sequence);
        var replay = new AgentEventMessage("codex", grant.SourceInstance, "task-1", RelayStore.MaxInboundEventKeys, "task_updated", at);
        Assert.Equal(RelayRouteResult.AlreadyApplied, router.RouteAdapterEvent(
            grant.Credential, ProtocolEnvelope.Create("replay", "agent_event", replay, at), at).Result);
    }

    [Fact]
    public void Structured_event_identity_prevents_slash_collisions_and_cross_source_ack()
    {
        var store = new RelayStore();
        var registration = new RegistrationService(store);
        var router = new RelayRouter(store, registration);
        var at = DateTimeOffset.Parse("2026-08-30T08:00:00Z");
        var first = new RegistrationGrant("a/b", "c", Convert.ToBase64String(new byte[32]), at, Enabled: true);
        var second = new RegistrationGrant("a", "b/c", Convert.ToBase64String(Enumerable.Repeat((byte)1, 32).ToArray()), at, Enabled: true);
        store.SaveGrant(first);
        store.SaveGrant(second);
        var firstMessage = new AgentEventMessage(first.SourceType, first.SourceInstance, "d", 1, "task_started", at);
        var secondMessage = new AgentEventMessage(second.SourceType, second.SourceInstance, "d", 1, "task_started", at);

        Assert.Equal(RelayRouteResult.Queued, router.RouteAdapterEvent(first.Credential,
            ProtocolEnvelope.Create("event-first", "agent_event", firstMessage, at), at).Result);
        Assert.Equal(RelayRouteResult.Queued, router.RouteAdapterEvent(second.Credential,
            ProtocolEnvelope.Create("event-second", "agent_event", secondMessage, at), at).Result);
        Assert.Equal("acknowledged", router.AcknowledgeInbound(new EventAcknowledgementRequest(
            first.SourceType, first.SourceInstance, new[] { new EventAcknowledgement("d", 1) }), at));

        var remaining = Assert.Single(router.DrainInbound(consume: false)).DeserializePayload<AgentEventMessage>();
        Assert.Equal(second.SourceType, remaining.SourceType);
        Assert.Equal(second.SourceInstance, remaining.SourceInstance);
    }

    [Fact]
    public void Archive_prepare_rejects_pending_delivery_without_mutating_relay_state()
    {
        var store = new RelayStore();
        var registration = new RegistrationService(store);
        var router = new RelayRouter(store, registration);
        var at = DateTimeOffset.Parse("2026-08-30T08:00:00Z");
        var grant = Approve(registration, at);
        router.TouchAdapterOnline(grant, at);
        router.SetAllowedTargets("codex", new[] { "opaque-project" });
        var request = new DispatchTaskRequest("dispatch-1", "task-1", "Ship it", null, "normal", null, "opaque-project");
        Assert.Equal(RelayRouteResult.Accepted, router.RouteDispatch(grant.Credential, request, at).Result);
        var before = store.Snapshot;

        var result = router.PrepareArchive(new AgentArchivePrepareRequest(
            "batch-1", new[] { Item(grant, request) }, Hash()), at);

        Assert.Equal("rejected", result.Result);
        Assert.Contains("pending", result.SafeError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before.Outbound, store.Snapshot.Outbound);
        Assert.Equal(before.DispatchReceipts, store.Snapshot.DispatchReceipts);
        Assert.Empty(store.Snapshot.ArchiveBatches);
    }

    [Fact]
    public void Archive_prepare_sync_commit_and_replay_are_idempotent_across_store_restart()
    {
        var state = new InMemoryRelayStateStore();
        var firstStore = new RelayStore(state);
        var firstRegistration = new RegistrationService(firstStore);
        var router = new RelayRouter(firstStore, firstRegistration);
        var at = DateTimeOffset.Parse("2026-08-30T08:00:00Z");
        var grant = Approve(firstRegistration, at);
        firstStore.UpdatePermissions(grant.SourceType, grant.SourceInstance, new[] { "opaque-project" }, enabled: true);
        router.TouchAdapterOnline(grant, at);
        var request = new DispatchTaskRequest("dispatch-1", "task-1", "Ship it", null, "normal", null, "opaque-project");
        Assert.Equal(RelayRouteResult.Accepted, router.RouteDispatch(grant.Credential, request, at).Result);
        _ = router.DrainOutbound(grant.Credential, at);
        Assert.Equal(RelayRouteResult.Queued, router.RouteAdapterEvent(grant.Credential,
            ProtocolEnvelope.Create("event-1", "agent_event", new AgentEventMessage(
                "codex", grant.SourceInstance, "task-1", 2, "task_completed", at)), at).Result);
        Assert.Equal("acknowledged", router.AcknowledgeInbound(new EventAcknowledgementRequest(
            "codex", grant.SourceInstance, new[] { new EventAcknowledgement("task-1", 2) }), at));

        var item = Item(grant, request);
        var prepared = router.PrepareArchive(new AgentArchivePrepareRequest("batch-1", new[] { item }, Hash()), at);
        Assert.Equal("accepted", prepared.Result);
        Assert.Equal(RelayArchiveBatchPhase.AwaitingAdapterPrepare, Assert.Single(firstStore.Snapshot.ArchiveBatches).Phase);

        var restartedStore = new RelayStore(state);
        var restartedRegistration = new RegistrationService(restartedStore);
        var restartedRouter = new RelayRouter(restartedStore, restartedRegistration);
        var sync = restartedRouter.SyncMaintenance(grant.Credential, new AdapterMaintenanceSyncRequest(
            "codex", grant.SourceInstance, null, null, null,
            new AgentCapacityCounter("adapter_journal", 1, 128, 1)), at);
        Assert.Equal("prepare", sync.Result);
        Assert.Equal("batch-1", sync.BatchId);

        var preparedAck = restartedRouter.SyncMaintenance(grant.Credential, new AdapterMaintenanceSyncRequest(
            "codex", grant.SourceInstance, "batch-1", "prepare", null,
            new AgentCapacityCounter("adapter_journal", 1, 128, 1)), at);
        Assert.Equal("prepared", preparedAck.Result);
        Assert.Equal("accepted", restartedRouter.CommitArchive(new AgentArchiveCommitRequest("batch-1", Hash()), at).Result);

        var committedAck = restartedRouter.SyncMaintenance(grant.Credential, new AdapterMaintenanceSyncRequest(
            "codex", grant.SourceInstance, "batch-1", "commit", null,
            new AgentCapacityCounter("adapter_journal", 0, 128, 1)), at);
        Assert.Equal("committed", committedAck.Result);
        Assert.Equal(RelayArchiveBatchPhase.Completed, Assert.Single(restartedStore.Snapshot.ArchiveBatches).Phase);
        Assert.Single(restartedStore.Snapshot.ArchiveTombstones);
        Assert.Empty(restartedStore.Snapshot.DispatchReceipts);
        Assert.Empty(restartedStore.Snapshot.InboundEventWatermarks);

        var replay = restartedRouter.RouteAdapterEvent(grant.Credential,
            ProtocolEnvelope.Create("event-replay", "agent_event", new AgentEventMessage(
                "codex", grant.SourceInstance, "task-1", 2, "task_completed", at.AddMinutes(1))), at.AddMinutes(1));
        Assert.Equal(RelayRouteResult.AlreadyApplied, replay.Result);
        Assert.Equal("committed", restartedRouter.SyncMaintenance(grant.Credential, new AdapterMaintenanceSyncRequest(
            "codex", grant.SourceInstance, "batch-1", "commit", null,
            new AgentCapacityCounter("adapter_journal", 0, 128, 1)), at).Result);
    }

    private static RegistrationGrant Approve(RegistrationService service, DateTimeOffset at)
    {
        var pending = service.Request(new AdapterRegistrationRequest("codex", "Codex", "1.0"), at);
        return service.Approve(pending.RequestId, at.AddSeconds(1));
    }

    private static AgentArchiveProtocolItem Item(RegistrationGrant grant, DispatchTaskRequest request) => new(
        grant.SourceType, grant.SourceInstance, request.TodoId, request.DispatchRequestId, 2, "completed",
        DateTimeOffset.Parse("2026-08-30T07:00:00Z"), "execution-1", Hash());

    private static string Hash() => new('A', 64);
}
