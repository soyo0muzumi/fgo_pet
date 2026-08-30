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
}
