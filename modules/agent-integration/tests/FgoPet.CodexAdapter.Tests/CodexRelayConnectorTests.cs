using System.Security.Cryptography;
using FgoPet.AgentProtocol;
using FgoPet.AgentProtocol.Messages;
using FgoPet.AgentRuntime;
using FgoPet.CodexAdapter.Relay;
using Xunit;

namespace FgoPet.CodexAdapter.Tests;

public sealed class CodexRelayConnectorTests
{
    [Fact]
    public async Task Pairing_persists_the_credential_before_authentication_and_reuses_it_after_restart()
    {
        var store = new MemoryIdentityStore();
        var transport = new PairingTransport(store);
        var connector = NewConnector(store, transport);

        Assert.Equal(AdapterConnectionStatus.ApprovalRequired, (await connector.EnsureAuthenticatedAsync()).Status);
        Assert.Equal("request-1", store.State.RequestId);
        transport.Approved = true;
        Assert.Equal(AdapterConnectionStatus.Connected, (await connector.EnsureAuthenticatedAsync()).Status);
        Assert.Equal(transport.Credential, store.State.Credential);

        var restarted = NewConnector(store, transport);
        Assert.Equal(AdapterConnectionStatus.Connected, (await restarted.EnsureAuthenticatedAsync()).Status);
        Assert.Equal(1, transport.RegistrationRequests);
    }

    [Fact]
    public async Task Revocation_clears_only_pairing_data_and_does_not_reregister_in_the_same_session()
    {
        var store = new MemoryIdentityStore();
        var transport = new PairingTransport(store) { Revoked = true };
        store.State = store.State with { Credential = transport.Credential, RequestId = "request-1" };
        var source = store.State.SourceInstanceId;
        var oldNonce = store.State.RequestNonce;
        var connector = NewConnector(store, transport);

        Assert.Equal(AdapterConnectionStatus.Revoked, (await connector.EnsureAuthenticatedAsync()).Status);
        Assert.Equal(AdapterConnectionStatus.Revoked, (await connector.EnsureAuthenticatedAsync()).Status);
        Assert.Null(store.State.Credential);
        Assert.Null(store.State.RequestId);
        Assert.Equal(source, store.State.SourceInstanceId);
        Assert.NotEqual(oldNonce, store.State.RequestNonce);
        Assert.Equal(0, transport.RegistrationRequests);

        Assert.Equal(AdapterConnectionStatus.ApprovalRequired, (await NewConnector(store, transport).EnsureAuthenticatedAsync()).Status);
        Assert.Equal(1, transport.RegistrationRequests);
    }

    [Fact]
    public async Task Temporary_disconnect_keeps_the_durable_credential()
    {
        var store = new MemoryIdentityStore();
        var transport = new PairingTransport(store) { Offline = true };
        store.State = store.State with { Credential = transport.Credential };
        var connector = NewConnector(store, transport);

        Assert.Equal(AdapterConnectionStatus.RelayOffline, (await connector.EnsureAuthenticatedAsync()).Status);
        Assert.Equal(transport.Credential, store.State.Credential);
        transport.Offline = false;
        Assert.Equal(AdapterConnectionStatus.Connected, (await connector.EnsureAuthenticatedAsync()).Status);
        Assert.Equal(0, transport.RegistrationRequests);
    }

    [Fact]
    public async Task Approval_for_another_instance_cannot_be_persisted()
    {
        var store = new MemoryIdentityStore();
        var transport = new PairingTransport(store) { Approved = true, WrongInstance = true };
        var connector = NewConnector(store, transport);
        await connector.EnsureAuthenticatedAsync();

        var result = await connector.EnsureAuthenticatedAsync();

        Assert.NotEqual(AdapterConnectionStatus.Connected, result.Status);
        Assert.Null(store.State.Credential);
        Assert.Equal(0, transport.AuthenticationRequests);
    }

    [Fact]
    public async Task Credential_save_failure_does_not_consume_recoverable_approval()
    {
        var store = new MemoryIdentityStore();
        var transport = new PairingTransport(store);
        var connector = NewConnector(store, transport);
        await connector.EnsureAuthenticatedAsync();
        transport.Approved = true;
        store.FailCredentialSave = true;

        Assert.Equal(AdapterConnectionStatus.RelayOffline, (await connector.EnsureAuthenticatedAsync()).Status);
        Assert.Null(store.State.Credential);
        Assert.Equal(0, transport.AuthenticationRequests);
        store.FailCredentialSave = false;
        Assert.Equal(AdapterConnectionStatus.Connected, (await connector.EnsureAuthenticatedAsync()).Status);
        Assert.Equal(1, transport.RegistrationRequests);
    }

    private static CodexRelayConnector NewConnector(MemoryIdentityStore store, PairingTransport transport) =>
        new(store, transport, _ => Task.FromResult(new RelayBootstrapResult(RelayBootstrapStatus.Ready, null)));

    [Fact]
    public async Task Failed_revocation_cleanup_is_retried_without_authenticating_or_registering_again()
    {
        var store = new MemoryIdentityStore { FailRevocationSave = true };
        var transport = new PairingTransport(store) { Revoked = true };
        store.State = store.State with { Credential = transport.Credential, RequestId = "request-1" };
        var connector = NewConnector(store, transport);

        Assert.Equal(AdapterConnectionStatus.RelayOffline, (await connector.EnsureAuthenticatedAsync()).Status);
        Assert.NotNull(store.State.Credential);
        store.FailRevocationSave = false;
        Assert.Equal(AdapterConnectionStatus.Revoked, (await connector.EnsureAuthenticatedAsync()).Status);
        Assert.Null(store.State.Credential);
        Assert.Null(store.State.RequestId);
        Assert.Equal(1, transport.AuthenticationRequests);
        Assert.Equal(0, transport.RegistrationRequests);
    }

    [Fact]
    public async Task Dispatch_acknowledgements_are_chunked_to_512_ids()
    {
        var store = new MemoryIdentityStore { State = new("source-1", new string('a', 64),
            null) };
        var transport = new PairingTransport(store);
        store.State = store.State with { Credential = transport.Credential };
        var connector = NewConnector(store, transport);

        Assert.Equal(AdapterConnectionStatus.Connected, (await connector.EnsureAuthenticatedAsync()).Status);
        var result = await connector.AcknowledgeDispatchesAsync(
            Enumerable.Range(0, 513).Select(index => "dispatch-" + index).ToArray());

        Assert.Equal("acknowledged", result);
        Assert.Equal(new[] { 512, 1 }, transport.DispatchAcknowledgementBatchSizes);
    }

    private sealed class MemoryIdentityStore : IAdapterIdentityStore
    {
        public AdapterIdentityState State { get; set; } = new("source-1", new string('a', 64));
        public bool FailCredentialSave { get; set; }
        public bool FailRevocationSave { get; set; }
        public AdapterIdentityState LoadOrCreate() => State;
        public bool TrySave(AdapterIdentityState expected, AdapterIdentityState state)
        {
            if (FailCredentialSave && state.Credential is not null) throw new IOException("storage_unavailable");
            if (FailRevocationSave && expected.Credential is not null && state.Credential is null) throw new IOException("storage_unavailable");
            if (State != expected) return false;
            State = state;
            return true;
        }
    }

    private sealed class PairingTransport(MemoryIdentityStore store) : IAdapterRelayTransport
    {
        public string Credential { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        public bool Approved { get; set; }
        public bool Revoked { get; set; }
        public bool Offline { get; set; }
        public bool WrongInstance { get; set; }
        public int RegistrationRequests { get; private set; }
        public int AuthenticationRequests { get; private set; }
        public List<int> DispatchAcknowledgementBatchSizes { get; } = [];

        public Task<ProtocolEnvelope> SendAsync(ProtocolEnvelope request, AuthenticateRequest? authentication = null,
            CancellationToken cancellationToken = default)
        {
            if (Offline) throw new IOException("unavailable");
            if (request.MessageType == "authenticate")
            {
                AuthenticationRequests++;
                var auth = request.DeserializePayload<AuthenticateRequest>();
                Assert.Equal(Credential, store.State.Credential); // Must have been saved before consumption.
                Assert.Equal("source-1", auth.SourceInstanceId);
                Assert.Equal(Credential, auth.Credential);
                return Task.FromResult(ProtocolEnvelope.Create(request.MessageId, "authenticate", new { result = Revoked ? "revoked" : "authenticated" }));
            }
            if (request.MessageType == "dispatch_ack")
            {
                var acknowledgement = request.DeserializePayload<DispatchAcknowledgementRequest>();
                DispatchAcknowledgementBatchSizes.Add(acknowledgement.DispatchRequestIds.Count);
                return Task.FromResult(ProtocolEnvelope.Create(request.MessageId, "dispatch_ack", new { result = "acknowledged" }));
            }
            if (request.MessageType == "registration_request")
            {
                RegistrationRequests++;
                var registration = request.DeserializePayload<RegistrationRequestMessage>();
                Assert.Equal("source-1", registration.SourceInstanceId);
                Assert.Equal(store.State.RequestNonce, registration.RequestNonce);
            }
            else
            {
                var poll = request.DeserializePayload<RegistrationStatusRequest>();
                Assert.Equal("request-1", poll.RequestId);
                Assert.Equal(store.State.RequestNonce, poll.RequestNonce);
            }
            var approved = Approved && request.MessageType == "registration_status";
            return Task.FromResult(ProtocolEnvelope.Create(request.MessageId, "registration_status",
                new RegistrationStatusResponse(approved ? "approved" : "pending", "request-1",
                    WrongInstance && approved ? "different-source" : "source-1", approved ? Credential : null, null)));
        }
    }
}
