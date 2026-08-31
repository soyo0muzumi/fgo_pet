using System.Security.Cryptography;
using FgoPet.AgentProtocol.Messages;
using FgoPet.AgentRelay.Storage;

namespace FgoPet.AgentRelay.Registration;

public enum RegistrationDecision
{
    Approve,
    Reject,
}

public sealed class RegistrationService
{
    private static readonly TimeSpan ApprovalLifetime = TimeSpan.FromMinutes(10);
    private readonly RelayStore _store;

    public RegistrationService(RelayStore store) => _store = store;

    public PendingRegistration Request(AdapterRegistrationRequest request, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(request);
        var pending = new PendingRegistration(
            CreateRequestId(),
            request.SourceType,
            request.DisplayName,
            request.Version,
            at,
            at.Add(ApprovalLifetime),
            CreateSourceInstance(request.SourceType));
        _store.AddPending(pending);
        return pending;
    }

    public PendingRegistration Request(RegistrationRequestMessage request, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _store.GetOrAddPending(
            request.SourceType,
            request.SourceInstanceId,
            request.RequestNonce,
            at,
            () => new PendingRegistration(
                CreateRequestId(),
                request.SourceType,
                request.DisplayName,
                request.AdapterVersion,
                at,
                at.Add(ApprovalLifetime),
                request.SourceInstanceId,
                request.RequestNonce));
    }

    public RegistrationGrant Approve(string requestId, DateTimeOffset at)
    {
        var pending = _store.GetPending(requestId, at)
            ?? throw new InvalidOperationException("The pairing request is missing or expired.");
        if (!string.Equals(pending.Decision, "pending", StringComparison.Ordinal))
            throw new InvalidOperationException("The pairing request has already been decided.");

        var grant = new RegistrationGrant(
            pending.SourceType,
            string.IsNullOrWhiteSpace(pending.SourceInstance) ? CreateSourceInstance(pending.SourceType) : pending.SourceInstance,
            CreateCredential(),
            at,
            Enabled: string.IsNullOrEmpty(pending.RequestNonce),
            AllowedTargetIds: Array.Empty<string>(),
            DisplayName: pending.DisplayName,
            Version: pending.Version,
            RequestId: pending.RequestId,
            RequestNonce: pending.RequestNonce);
        _store.ApprovePending(pending.RequestId, grant, at);
        return grant;
    }

    public void Reject(string requestId, DateTimeOffset at)
    {
        var pending = _store.GetPending(requestId, at)
            ?? throw new InvalidOperationException("The pairing request is missing or expired.");
        if (!string.Equals(pending.Decision, "pending", StringComparison.Ordinal))
            throw new InvalidOperationException("The pairing request has already been decided.");
        _store.SetPendingDecision(pending.RequestId, "rejected", at);
    }

    public RegistrationStatusResponse Poll(RegistrationStatusRequest request, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(request);
        var pending = _store.GetPending(request.RequestId, at);
        if (pending is null)
        {
            return new RegistrationStatusResponse("expired", request.RequestId, request.SourceInstanceId, null, "request_missing_or_expired");
        }

        if (!string.Equals(pending.SourceInstance, request.SourceInstanceId, StringComparison.Ordinal)
            || !string.Equals(pending.RequestNonce, request.RequestNonce, StringComparison.Ordinal))
        {
            return new RegistrationStatusResponse("unauthorized", request.RequestId, null, null, "request_identity_mismatch");
        }

        var status = pending.Decision switch
        {
            "approved" => "approved",
            "rejected" => "rejected",
            "revoked" => "revoked",
            "expired" => "expired",
            _ => "pending",
        };
        var credential = status == "approved" && !pending.CredentialConsumed ? pending.Credential : null;
        return new RegistrationStatusResponse(status, pending.RequestId, pending.SourceInstance, credential, null);
    }

    public RegistrationGrant Authenticate(string credential, DateTimeOffset at)
    {
        _ = at;
        var grant = _store.AuthenticateAndConsume(credential)
            ?? throw new UnauthorizedAccessException("The Agent credential is not valid.");
        return grant;
    }

    public RegistrationGrant Authenticate(string sourceType, string sourceInstance, string credential, DateTimeOffset at)
    {
        _ = at;
        var grant = _store.AuthenticateAndConsume(sourceType, sourceInstance, credential, out var revoked);
        if (grant is null && revoked) throw new RevokedRegistrationException();
        if (grant is null) throw new UnauthorizedAccessException("The Agent credential is not valid.");
        return grant;
    }

    public IReadOnlyList<PendingSourceDto> ListPendingSources(DateTimeOffset at) =>
        _store.ListPending(at).Select(item => new PendingSourceDto(
            item.RequestId,
            item.SourceType,
            item.DisplayName,
            item.SourceInstance,
            item.Version,
            item.RequestedAt,
            item.ExpiresAt)).ToArray();

    public IReadOnlyList<ApprovedSourceDto> ListSources(DateTimeOffset at, Func<RegistrationGrant, bool>? online = null) =>
        _store.ListGrants().Select(item => new ApprovedSourceDto(
            item.SourceType,
            item.DisplayName,
            item.SourceInstance,
            item.Version,
            item.ApprovedAt,
            item.Enabled,
            item.Targets,
            online?.Invoke(item) ?? false)).ToArray();

    public RegistrationGrant? GetGrant(string sourceType) => _store.GetGrant(sourceType);
    public RegistrationGrant? GetGrant(string sourceType, string sourceInstance) => _store.GetGrant(sourceType, sourceInstance);
    public bool Revoke(string sourceType) => _store.Revoke(sourceType);
    public bool Revoke(string sourceType, string sourceInstance) => _store.Revoke(sourceType, sourceInstance);

    private static string CreateSourceInstance(string sourceType) =>
        $"{sourceType.Trim()}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant()}";

    private static string CreateRequestId() => Guid.NewGuid().ToString("N");
    private static string CreateNonce() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private static string CreateCredential() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
}

public sealed class RevokedRegistrationException : UnauthorizedAccessException
{
    public RevokedRegistrationException() : base("The Agent registration has been revoked.") { }
}
