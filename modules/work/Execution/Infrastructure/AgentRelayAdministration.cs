using System.Text.Json;
using FgoPet.AgentProtocol;
using FgoPet.AgentProtocol.Messages;
using FgoPet.Core.Agents;

namespace FgoPet.Infrastructure.Agents;

public sealed class AgentRelayAdministration : IAgentRelayAdministration
{
    private readonly AgentControlClient _client;
    private readonly IAgentRepository? _agents;
    private readonly AgentEventProjector? _projector;
    private readonly Func<Action, Task> _dispatchToUi;

    public AgentRelayAdministration(
        AgentControlClient client,
        IAgentRepository? agents = null,
        AgentEventProjector? projector = null,
        Func<Action, Task>? dispatchToUi = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _agents = agents;
        _projector = projector;
        _dispatchToUi = dispatchToUi ?? (action =>
        {
            action();
            return Task.CompletedTask;
        });
    }

    public Task<AgentRelaySnapshot> TestConnectionAsync(CancellationToken cancellationToken = default) => GetSnapshotAsync(cancellationToken);

    public async Task<AgentRelaySnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var connection = (await SendAsync("connection_test", new { }, cancellationToken).ConfigureAwait(false))
                .DeserializePayload<RelayConnectionTestResponse>();
            if (connection.ProtocolVersion != ProtocolEnvelope.CurrentProtocolVersion)
                throw new AgentRelayException("version_mismatch");
            var pending = (await SendAsync("pending_sources", new { }, cancellationToken).ConfigureAwait(false))
                .Payload.GetProperty("sources").Deserialize<PendingSourceDto[]>(ProtocolEnvelope.JsonOptions)!;
            var approved = (await SendAsync("list_sources", new { }, cancellationToken).ConfigureAwait(false))
                .Payload.GetProperty("sources").Deserialize<ApprovedSourceDto[]>(ProtocolEnvelope.JsonOptions)!;
            var state = !connection.RelayOnline ? AgentRelayConnectionState.RelayOffline
                : connection.AdapterOnline ? AgentRelayConnectionState.Connected
                : pending.Length > 0 ? AgentRelayConnectionState.AwaitingApproval : AgentRelayConnectionState.AdapterOffline;
            return new(state, connection.RelayOnline, connection.AppOnline, connection.AdapterOnline, connection.ObservedAtUtc,
                pending.Select(p => new AgentPendingSource(p.RequestId, p.SourceType, p.SourceInstanceId, p.DisplayName,
                    p.AdapterVersion, p.RequestedAtUtc, p.ExpiresAtUtc)).ToArray(),
                approved.Select(p => new AgentApprovedSource(p.SourceType, p.SourceInstanceId, p.DisplayName,
                    p.AdapterVersion, p.Enabled, p.AllowedTargetIds, p.IsOnline)).ToArray());
        }
        catch (AgentRelayException error) { return Failure(error.SafeError); }
    }

    public async Task DecideRegistrationAsync(string requestId, bool approve, CancellationToken cancellationToken = default) =>
        _ = await SendAsync("decide_registration", new RegistrationDecisionRequest(requestId, approve ? "approve" : "reject"), cancellationToken).ConfigureAwait(false);

    public async Task UpdatePermissionsAsync(string sourceType, string sourceInstanceId, IReadOnlyList<string> targetIds, bool enabled, CancellationToken cancellationToken = default) =>
        _ = await SendAsync("update_permissions", new UpdatePermissionsRequest(sourceType, sourceInstanceId, targetIds, enabled), cancellationToken).ConfigureAwait(false);

    public async Task RevokeSourceAsync(string sourceType, string sourceInstanceId, CancellationToken cancellationToken = default)
    {
        // The acknowledgement is the authorization boundary: only after Relay
        // confirms revocation may the App synthesize local cancellation events.
        _ = await SendAsync("revoke_source", new RevokeSourceRequest(sourceType, sourceInstanceId), cancellationToken).ConfigureAwait(false);

        if (_agents is null || _projector is null)
        {
            return;
        }

        var occurredAt = DateTimeOffset.UtcNow;
        var activeExecutions = _agents.ListNonTerminalExecutions()
            .Where(execution => string.Equals(execution.SourceType, sourceType, StringComparison.Ordinal)
                && string.Equals(execution.SourceInstance, sourceInstanceId, StringComparison.Ordinal))
            .ToArray();
        await _dispatchToUi(() =>
        {
            foreach (var execution in activeExecutions)
            {
                // A revoked adapter can no longer emit task_cancelled. A per-task
                // long.MaxValue receipt is safe in the SQLite INTEGER range and
                // dominates any late adapter sequence without colliding across the
                // distinct task identities being cancelled here.
                _projector.Apply(new AgentEvent(
                    execution.SourceType,
                    execution.SourceInstance,
                    execution.TaskId,
                    long.MaxValue,
                    AgentEventType.TaskCancelled,
                    occurredAt,
                    summary: "来源已撤销，任务已在本地取消。",
                    TodoId: execution.TodoId,
                    DispatchRequestId: execution.DispatchRequestId));
            }
        }).ConfigureAwait(false);
    }

    public async Task<AgentMaintenanceStatus> GetMaintenanceStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await SendAsync("maintenance_status", new { }, cancellationToken).ConfigureAwait(false);
            var payload = response.DeserializePayload<AgentMaintenanceStatusResponse>();
            return new(
                payload.Counters.Select(counter => new AgentMaintenanceCounter(
                    counter.Name, counter.Used, counter.Limit, counter.Archivable)).ToArray(),
                payload.OldestArchivableAt,
                payload.ActiveBatchId,
                payload.SafeError);
        }
        catch (AgentRelayException error)
        {
            return AgentMaintenanceStatus.Empty with { SafeError = error.SafeError };
        }
    }

    public async Task<AgentArchivePrepareResult> PrepareArchiveAsync(
        AgentArchiveBatch batch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        try
        {
            var items = batch.Candidates.Select(candidate => new AgentArchiveProtocolItem(
                candidate.Identity.SourceType,
                candidate.Identity.SourceInstance,
                candidate.Identity.TaskId,
                candidate.Identity.DispatchRequestId,
                candidate.Identity.FinalSequence,
                ToWireStatus(candidate.Identity.FinalStatus),
                candidate.EndedAt,
                candidate.ExecutionId,
                candidate.SummarySha256)).ToArray();
            var response = await SendAsync(
                "archive_prepare",
                new AgentArchivePrepareRequest(batch.BatchId, items, batch.BatchSha256),
                cancellationToken).ConfigureAwait(false);
            return new(
                ReadResult(response) ?? "rejected",
                batch.BatchId,
                batch.BatchSha256,
                ReadOptionalString(response.Payload, "safe_error"));
        }
        catch (AgentRelayException error)
        {
            return new("rejected", batch.BatchId, batch.BatchSha256, error.SafeError);
        }
    }

    public async Task<AgentArchiveCommitResult> CommitArchiveAsync(
        string batchId,
        string batchSha256,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await SendAsync(
                "archive_commit",
                new AgentArchiveCommitRequest(batchId, batchSha256),
                cancellationToken).ConfigureAwait(false);
            return new(
                ReadResult(response) ?? "rejected",
                batchId,
                batchSha256,
                ReadOptionalString(response.Payload, "safe_error"));
        }
        catch (AgentRelayException error)
        {
            return new("rejected", batchId, batchSha256, error.SafeError);
        }
    }

    private Task<ProtocolEnvelope> SendAsync(string type, object payload, CancellationToken cancellationToken) =>
        _client.SendAsync(ProtocolEnvelope.Create(Guid.NewGuid().ToString("N"), type, payload), cancellationToken);

    private static string? ReadResult(ProtocolEnvelope response) =>
        response.Payload.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.String
            ? result.GetString()
            : null;

    private static string? ReadOptionalString(JsonElement payload, string propertyName) =>
        payload.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string ToWireStatus(AgentExecutionStatus status) => status switch
    {
        AgentExecutionStatus.Completed => "completed",
        AgentExecutionStatus.Failed => "failed",
        AgentExecutionStatus.Cancelled => "cancelled",
        _ => throw new ArgumentException("Archive batches require terminal executions.", nameof(status)),
    };

    internal static AgentRelaySnapshot Failure(string error) => new(error switch
    {
        "version_mismatch" => AgentRelayConnectionState.VersionMismatch,
        "authentication_failed" => AgentRelayConnectionState.AuthenticationFailed,
        _ => AgentRelayConnectionState.RelayOffline,
    }, false, false, false, DateTimeOffset.UtcNow, [], [], error);
}
