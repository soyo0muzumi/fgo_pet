using System.Security.Cryptography;
using FgoPet.AgentProtocol.Messages;
using FgoPet.AgentRelay.Storage;

namespace FgoPet.AgentRelay.Registration;

public sealed class RegistrationService
{
    private static readonly TimeSpan ApprovalLifetime = TimeSpan.FromMinutes(10);
    private readonly RelayStore _store;

    public RegistrationService(RelayStore store) => _store = store;

    public PendingRegistration Request(AdapterRegistrationRequest request, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(request);
        var pending = new PendingRegistration(
            CreateToken(), request.SourceType, request.DisplayName, request.Version, at, at.Add(ApprovalLifetime));
        _store.AddPending(pending);
        return pending;
    }

    public RegistrationGrant Approve(string requestId, DateTimeOffset at)
    {
        var pending = _store.GetPending(requestId, at)
            ?? throw new InvalidOperationException("The pairing request is missing or expired.");
        _store.RemovePending(requestId);
        var grant = new RegistrationGrant(pending.SourceType, CreateSourceInstance(pending.SourceType), CreateToken(), at);
        _store.SaveGrant(grant);
        return grant;
    }

    public RegistrationGrant Authenticate(string credential, DateTimeOffset at)
    {
        _ = at;
        return _store.Authenticate(credential)
            ?? throw new UnauthorizedAccessException("The Agent credential is not valid.");
    }

    public RegistrationGrant? GetGrant(string sourceType) => _store.GetGrant(sourceType);

    public bool Revoke(string sourceType) => _store.Revoke(sourceType);

    private static string CreateSourceInstance(string sourceType) =>
        $"{sourceType.Trim()}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant()}";

    private static string CreateToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
}
