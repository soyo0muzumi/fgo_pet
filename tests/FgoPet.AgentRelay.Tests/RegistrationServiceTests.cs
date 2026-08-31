using FgoPet.AgentProtocol.Messages;
using FgoPet.AgentRelay.Registration;
using FgoPet.AgentRelay.Storage;
using Xunit;

namespace FgoPet.AgentRelay.Tests;

public sealed class RegistrationServiceTests
{
    [Fact]
    public void Registration_requires_approval_and_grants_stable_identity()
    {
        var store = new RelayStore();
        var service = new RegistrationService(store);
        var at = DateTimeOffset.Parse("2026-08-30T08:00:00Z");
        var pending = service.Request(new AdapterRegistrationRequest("codex", "Codex", "1.0"), at);

        Assert.Equal(TimeSpan.FromMinutes(10), pending.ExpiresAt - at);
        var grant = service.Approve(pending.RequestId, at.AddMinutes(1));
        var authenticated = service.Authenticate(grant.Credential, at.AddMinutes(2));

        Assert.Equal(grant.SourceInstance, authenticated.SourceInstance);
        Assert.Equal("codex", authenticated.SourceType);
        Assert.Equal(grant.SourceInstance, service.GetGrant("codex")!.SourceInstance);
    }

    [Fact]
    public void Expiry_and_revoke_immediately_invalidate_old_credentials()
    {
        var store = new RelayStore();
        var service = new RegistrationService(store);
        var at = DateTimeOffset.Parse("2026-08-30T08:00:00Z");
        var pending = service.Request(new AdapterRegistrationRequest("codex", "Codex", "1.0"), at);

        Assert.Throws<InvalidOperationException>(() => service.Approve(pending.RequestId, at.AddMinutes(11)));
        var second = service.Request(new AdapterRegistrationRequest("codex", "Codex", "1.0"), at.AddMinutes(12));
        var grant = service.Approve(second.RequestId, at.AddMinutes(13));
        Assert.True(service.Revoke("codex"));
        Assert.Throws<UnauthorizedAccessException>(() => service.Authenticate(grant.Credential, at.AddMinutes(14)));
    }

    [Fact]
    public void Versioned_pairing_is_idempotent_and_credential_is_consumed_by_first_authentication()
    {
        var service = new RegistrationService(new RelayStore());
        var at = DateTimeOffset.Parse("2026-08-30T08:00:00Z");
        var request = new RegistrationRequestMessage(
            "codex", "Codex", "instance-1", "1.0", "1", new string('a', 64));

        var first = service.Request(request, at);
        var replay = service.Request(request, at.AddSeconds(1));
        var grant = service.Approve(first.RequestId, at.AddSeconds(2));
        var beforeAuth = service.Poll(new RegistrationStatusRequest(first.RequestId, "instance-1", request.RequestNonce), at.AddSeconds(3));
        var authenticated = service.Authenticate("codex", "instance-1", grant.Credential, at.AddSeconds(4));
        var afterAuth = service.Poll(new RegistrationStatusRequest(first.RequestId, "instance-1", request.RequestNonce), at.AddSeconds(5));

        Assert.Equal(first, replay);
        Assert.Equal("approved", beforeAuth.Status);
        Assert.Equal(grant.Credential, beforeAuth.Credential);
        Assert.Equal(grant, authenticated);
        Assert.Equal("approved", afterAuth.Status);
        Assert.Null(afterAuth.Credential);
    }

    [Fact]
    public void Wrong_nonce_rejects_poll_and_revocation_cancels_queued_work()
    {
        var service = new RegistrationService(new RelayStore());
        var at = DateTimeOffset.Parse("2026-08-30T08:00:00Z");
        var request = new RegistrationRequestMessage(
            "codex", "Codex", "instance-1", "1.0", "1", new string('b', 64));
        var pending = service.Request(request, at);
        var grant = service.Approve(pending.RequestId, at.AddSeconds(1));

        var wrongNonce = service.Poll(new RegistrationStatusRequest(pending.RequestId, "instance-1", new string('c', 64)), at.AddSeconds(2));
        Assert.Equal("unauthorized", wrongNonce.Status);
        Assert.Null(wrongNonce.Credential);

        Assert.True(service.Revoke("codex", "instance-1"));
        Assert.Throws<RevokedRegistrationException>(() => service.Authenticate("codex", "instance-1", grant.Credential, at.AddSeconds(3)));
        Assert.Equal("revoked", service.Poll(new RegistrationStatusRequest(pending.RequestId, "instance-1", request.RequestNonce), at.AddSeconds(4)).Status);
    }

    [Fact]
    public void Failed_persistence_does_not_publish_authoritative_mutation()
    {
        var stateStore = new FailingStateStore();
        var service = new RegistrationService(new RelayStore(stateStore));
        var at = DateTimeOffset.Parse("2026-08-30T08:00:00Z");
        var request = new RegistrationRequestMessage(
            "codex", "Codex", "instance-1", "1.0", "1", new string('d', 64));
        stateStore.Fail = true;

        Assert.Throws<IOException>(() => service.Request(request, at));
        stateStore.Fail = false;
        Assert.Empty(service.ListPendingSources(at));
    }

    private sealed class FailingStateStore : IRelayStateStore
    {
        private RelayState _state = RelayState.Empty;
        public bool Fail { get; set; }
        public RelayState Load() => _state;
        public void Save(RelayState state)
        {
            if (Fail) throw new IOException("save failed");
            _state = state;
        }
    }
}
