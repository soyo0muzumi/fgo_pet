using FgoPet.AgentProtocol;
using FgoPet.AgentProtocol.Messages;
using FgoPet.AgentRelay.Registration;
using FgoPet.AgentRelay.Routing;
using FgoPet.AgentRelay.Storage;
using Xunit;

namespace FgoPet.AgentRelay.Tests;

public sealed class RelayAuthorizationBoundaryTests
{
    [Fact]
    public void An_old_nonce_cannot_reopen_an_expired_or_revoked_request_or_replace_a_live_grant()
    {
        var store = new RelayStore();
        var registration = new RegistrationService(store);
        var now = DateTimeOffset.UtcNow;
        var request = Request('a');
        var first = registration.Request(request, now);
        var grant = registration.Approve(first.RequestId, now);
        Assert.Equal(first.RequestId, registration.Request(request, now.AddMinutes(11)).RequestId);
        var competing = registration.Request(Request('b'), now.AddMinutes(11));
        Assert.Throws<InvalidOperationException>(() => registration.Approve(competing.RequestId, now.AddMinutes(11)));
        Assert.Equal(grant.Credential, registration.GetGrant("codex", "source-1")!.Credential);
        registration.Revoke("codex", "source-1");
        Assert.Equal(first.RequestId, registration.Request(request, now.AddMinutes(12)).RequestId);
        Assert.Empty(registration.ListPendingSources(now.AddMinutes(12)));
        var fresh = registration.Request(Request('c'), now.AddMinutes(12));
        Assert.NotNull(registration.Approve(fresh.RequestId, now.AddMinutes(12)));
    }

    [Fact]
    public void Liveness_expires_and_a_reapproved_grant_cannot_inherit_an_old_session_heartbeat()
    {
        var store = new RelayStore();
        var registration = new RegistrationService(store);
        var router = new RelayRouter(store, registration);
        var now = DateTimeOffset.UtcNow;
        var pending = registration.Request(Request('a'), now);
        var grant = registration.Approve(pending.RequestId, now);
        router.TouchAppOnline(now);
        router.TouchAdapterOnline(grant, now);
        Assert.True(router.IsAppOnline(now));
        Assert.True(router.IsAdapterOnlineFor("codex", "source-1", now));
        Assert.False(router.IsAppOnline(now.AddSeconds(11)));
        Assert.False(router.IsAdapterOnlineFor("codex", "source-1", now.AddSeconds(11)));
        registration.Revoke("codex", "source-1");
        var fresh = registration.Request(Request('b'), now.AddSeconds(1));
        registration.Approve(fresh.RequestId, now.AddSeconds(1));
        router.TouchAdapterOnline(grant, now.AddSeconds(2));
        Assert.False(router.IsAdapterOnlineFor("codex", "source-1", now.AddSeconds(2)));
    }

    [Fact]
    public void Revoked_session_cannot_enqueue_or_take_work_after_its_grant_is_replaced()
    {
        var store = new RelayStore();
        var registration = new RegistrationService(store);
        var router = new RelayRouter(store, registration);
        var now = DateTimeOffset.UtcNow;
        var first = registration.Request(Request('a'), now);
        var grant = registration.Approve(first.RequestId, now);
        store.UpdatePermissions("codex", "source-1", ["target"], true);
        registration.Revoke("codex", "source-1");
        var second = registration.Request(Request('b'), now);
        registration.Approve(second.RequestId, now);
        store.UpdatePermissions("codex", "source-1", ["target"], true);
        var message = ProtocolEnvelope.Create("event", "agent_event", new AgentEventMessage("codex", "source-1", "task", 1, "task_started", now));
        Assert.Throws<UnauthorizedAccessException>(() => router.RouteAdapterEvent(grant, message, now));
        Assert.Throws<UnauthorizedAccessException>(() => router.DrainOutbound(grant, now));
        Assert.Equal(0, store.PendingInboundCount);
    }

    [Fact]
    public void Pending_capacity_is_bounded_without_displacing_existing_requests()
    {
        var registration = new RegistrationService(new RelayStore());
        var now = DateTimeOffset.UtcNow;
        var first = registration.Request(Request('a'), now);
        for (var index = 1; index < RelayStore.MaxRegistrationRecords; index++)
            registration.Request(Request('a') with { SourceInstanceId = "source-" + (index + 1) }, now);
        Assert.Throws<InvalidOperationException>(() => registration.Request(Request('b') with { SourceInstanceId = "overflow" }, now));
        Assert.Equal(first.RequestId, registration.Request(Request('a'), now).RequestId);
    }

    private static RegistrationRequestMessage Request(char nonce) =>
        new("codex", "Codex", "source-1", "1", "1", new string(nonce, 64));
}
