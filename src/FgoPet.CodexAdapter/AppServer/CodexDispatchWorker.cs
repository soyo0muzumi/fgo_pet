using System.Security.Cryptography;
using System.Text;
using FgoPet.AgentProtocol.Messages;
using FgoPet.AgentRuntime.Security;
using FgoPet.AgentRuntime.Storage;
using FgoPet.CodexAdapter.Relay;
using FgoPet.Core.Agents;

namespace FgoPet.CodexAdapter.AppServer;

public sealed record CodexDispatchRecord(DispatchTaskRequest Request, long Sequence, string State,
    string? ThreadId = null, AgentEventMessage? PendingEvent = null, AgentEventMessage? TerminalEvent = null);

/// <summary>Durable dedupe and terminal outbox. Interrupted work is reported, never silently restarted.</summary>
public sealed class CodexDispatchWorker
{
    private readonly ICodexRelayConnector _connector;
    private readonly ICodexTaskExecutor _executor;
    private readonly AtomicProtectedJsonStore<CodexDispatchRecord[]> _store;
    private readonly AtomicProtectedJsonStore<CodexArchiveState> _archiveStore;
    private readonly Dictionary<string, CodexDispatchRecord> _records;
    private CodexArchiveState _archiveState;
    private readonly ICodexWorkerDiagnostics _diagnostics;

    public CodexDispatchWorker(ICodexRelayConnector connector, ICodexTaskExecutor executor, string stateRoot,
        ISecretProtector? protector = null, ICodexWorkerDiagnostics? diagnostics = null)
    {
        _connector = connector;
        _executor = executor;
        _diagnostics = diagnostics ?? NullCodexWorkerDiagnostics.Instance;
        var identityKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(connector.SourceInstanceId)));
        _store = new(Path.Combine(stateRoot, "CodexAdapter", "dispatches-" + identityKey + ".v1.json"),
            protector ?? new DpapiSecretProtector());
        _archiveStore = new(Path.Combine(stateRoot, "CodexAdapter", "dispatch-archives-" + identityKey + ".v1.json"),
            protector ?? new DpapiSecretProtector());
        _records = _store.Load(() => [], records => records.Length <= 512 && records.All(r => r.Request is not null
            && r.Request.SourceInstanceId == connector.SourceInstanceId && r.Request.SourceType == "codex" && r.Sequence >= 0)
            && records.Select(r => r.Request.DispatchRequestId).Distinct().Count() == records.Length)
            .ToDictionary(r => r.Request.DispatchRequestId, StringComparer.Ordinal);
        _archiveState = _archiveStore.Load(
            () => CodexArchiveState.Empty,
            IsValidArchiveState);
        _archiveState = _archiveState with
        {
            Tombstones = _archiveState.Tombstones ?? Array.Empty<AgentArchiveProtocolItem>(),
        };
        RecoverCommittedArchive();
        foreach (var old in _records.Values.Where(r => r.State != "terminal" && r.State != "awaiting_acceptance").ToArray())
            SetEvent(old, "task_cancelled", "adapter_interrupted", terminal: true);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var connection = await _connector.EnsureAuthenticatedAsync(cancellationToken).ConfigureAwait(false);
                _diagnostics.Record("relay.authenticate", connection.Status.ToString().ToLowerInvariant(), connection.Error);
                if (connection.Status is AdapterConnectionStatus.Revoked or AdapterConnectionStatus.Rejected or AdapterConnectionStatus.VersionMismatch) return;
                if (connection.Status == AdapterConnectionStatus.Connected)
                {
                    await ProcessMaintenanceAsync(cancellationToken).ConfigureAwait(false);
                    foreach (var record in _records.Values.Where(r => r.PendingEvent is not null).ToArray())
                        await DeliverAsync(record, cancellationToken).ConfigureAwait(false);
                    IReadOnlyList<DispatchTaskRequest> requests;
                    try
                    {
                        requests = await _connector.PollDispatchesAsync(cancellationToken).ConfigureAwait(false);
                        _diagnostics.Record("dispatch.poll", requests.Count == 0 ? "empty" : $"received_{Math.Min(requests.Count, 512)}");
                    }
                    catch (Exception error)
                    {
                        _diagnostics.Record("dispatch.poll", "failed", CodexWorkerDiagnostics.ErrorCode(error));
                        throw;
                    }
                    var acknowledgedIds = new List<string>();
                    var journalFull = false;
                    foreach (var request in requests)
                    {
                        if (request.SourceType != "codex" || request.SourceInstanceId != _connector.SourceInstanceId)
                            throw new InvalidDataException("dispatch_identity_mismatch");
                        if (_records.TryGetValue(request.DispatchRequestId, out var existing))
                        {
                            if (existing.State == "terminal")
                                await ReplayTerminalAsync(existing, cancellationToken).ConfigureAwait(false);
                            acknowledgedIds.Add(request.DispatchRequestId);
                            continue;
                        }
                        if (IsArchivedDispatch(request.DispatchRequestId))
                        {
                            _diagnostics.Record("dispatch.replay", "archived", dispatchRequestId: request.DispatchRequestId);
                            acknowledgedIds.Add(request.DispatchRequestId);
                            continue;
                        }
                        if (_records.Count >= 512)
                        {
                            journalFull = true;
                            continue;
                        }
                        Save(new(request, 0, "queued"));
                        _diagnostics.Record("dispatch.queue", "ok", dispatchRequestId: request.DispatchRequestId);
                        acknowledgedIds.Add(request.DispatchRequestId);
                    }
                    if (acknowledgedIds.Count > 0)
                    {
                        try
                        {
                            await AcknowledgeDispatchesAsync(acknowledgedIds, cancellationToken).ConfigureAwait(false);
                            foreach (var id in acknowledgedIds)
                                _diagnostics.Record("dispatch.ack", "ok", dispatchRequestId: id);
                        }
                        catch (Exception error)
                        {
                            _diagnostics.Record("dispatch.ack", "failed", CodexWorkerDiagnostics.ErrorCode(error));
                            throw;
                        }
                    }
                    foreach (var record in _records.Values.Where(r => r.State == "queued").ToArray())
                        await ExecuteAsync(record, cancellationToken).ConfigureAwait(false);
                    if (journalFull) throw new InvalidDataException("dispatch_journal_full");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (InvalidDataException error) when (error.Message == "dispatch_journal_full")
            {
                // Do not silently discard work when the bounded journal is full.
                throw;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                // Keep pending delivery and retry after bounded backoff; stdout belongs to MCP.
                _diagnostics.Record("worker.loop", "failed", CodexWorkerDiagnostics.ErrorCode(error));
            }
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ExecuteAsync(CodexDispatchRecord record, CancellationToken cancellationToken)
    {
        var request = record.Request;
        if (!await _connector.IsDispatchAllowedAsync(request.TargetId, cancellationToken).ConfigureAwait(false))
        {
            _diagnostics.Record("target.permission", "denied", "target_permission_removed", request.DispatchRequestId);
            SetEvent(record, "task_cancelled", "target_permission_removed", true);
            return;
        }
        _diagnostics.Record("target.permission", "allowed", dispatchRequestId: request.DispatchRequestId);
        Save(record with { State = "running" }); // Persist before side effects, including process/thread creation.
        _diagnostics.Record("dispatch.execute", "started", dispatchRequestId: request.DispatchRequestId);
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var watching = WatchAuthorizationAsync(request.TargetId, execution);
        string result;
        try
        {
            result = await _executor.ExecuteAsync(request, async (kind, threadId) =>
            {
                var current = _records[request.DispatchRequestId] with { ThreadId = threadId };
                var updated = SetEvent(current, kind, kind == "attention_required" ? "interactive_approval_required" : "Codex " + (threadId ?? "working"), false);
                await DeliverAsync(updated, execution.Token).ConfigureAwait(false);
            }, execution.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { result = "task_cancelled"; }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException
            or System.Text.Json.JsonException or System.Threading.Channels.ChannelClosedException or InvalidOperationException
            or KeyNotFoundException or System.ComponentModel.Win32Exception or TimeoutException)
        {
            result = "task_failed";
            _diagnostics.Record("dispatch.execute", "failed", CodexWorkerDiagnostics.ErrorCode(error), request.DispatchRequestId);
        }
        finally
        {
            await execution.CancelAsync().ConfigureAwait(false);
            try { await watching.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        if (result == "awaiting_acceptance")
        {
            // The visible resume process owns the interactive turn now. Keep this
            // dispatch non-terminal so a restart can continue it instead of
            // converting a pending approval into a cancellation.
            Save(_records[request.DispatchRequestId] with { State = "awaiting_acceptance" });
            _diagnostics.Record("dispatch.state", "awaiting_acceptance", dispatchRequestId: request.DispatchRequestId);
            return;
        }

        _diagnostics.Record("dispatch.state", result, dispatchRequestId: request.DispatchRequestId);
        SetEvent(_records[request.DispatchRequestId], result, result == "task_cancelled" ? "execution_interrupted_or_authorization_changed" : result, true);
        if (!cancellationToken.IsCancellationRequested)
            await DeliverAsync(_records[request.DispatchRequestId], cancellationToken).ConfigureAwait(false);
    }

    private async Task WatchAuthorizationAsync(string targetId, CancellationTokenSource execution)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), execution.Token).ConfigureAwait(false);
                if (!await _connector.IsDispatchAllowedAsync(targetId, execution.Token).ConfigureAwait(false)) break;
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or InvalidDataException) { }
        await execution.CancelAsync().ConfigureAwait(false);
    }

    private CodexDispatchRecord SetEvent(CodexDispatchRecord record, string type, string summary, bool terminal)
    {
        var message = new AgentEventMessage("codex", _connector.SourceInstanceId, record.Request.DispatchRequestId,
            record.Sequence + 1, type, DateTimeOffset.UtcNow, Summary: summary, TodoId: record.Request.TodoId,
            DispatchRequestId: record.Request.DispatchRequestId, RemoteTaskId: record.ThreadId);
        var updated = record with
        {
            Sequence = message.Sequence,
            State = terminal ? "terminal" : record.State,
            PendingEvent = message,
            TerminalEvent = terminal ? message : record.TerminalEvent,
        };
        Save(updated);
        return updated;
    }

    private async Task DeliverAsync(CodexDispatchRecord record, CancellationToken cancellationToken)
    {
        if (record.PendingEvent is null) return;
        try
        {
            await _connector.SendEventAsync(record.PendingEvent, cancellationToken).ConfigureAwait(false);
            _diagnostics.Record("event.delivery", "ok", dispatchRequestId: record.Request.DispatchRequestId);
        }
        catch (Exception error)
        {
            _diagnostics.Record("event.delivery", "failed", CodexWorkerDiagnostics.ErrorCode(error), record.Request.DispatchRequestId);
            throw;
        }
        Save(record with { PendingEvent = null });
    }

    private async Task ReplayTerminalAsync(CodexDispatchRecord record, CancellationToken cancellationToken)
    {
        if (record.TerminalEvent is null) return;
        if (record.PendingEvent is null)
        {
            Save(record with { PendingEvent = record.TerminalEvent });
            record = _records[record.Request.DispatchRequestId];
        }
        await DeliverAsync(record, cancellationToken).ConfigureAwait(false);
    }

    private async Task AcknowledgeDispatchesAsync(
        IReadOnlyList<string> dispatchRequestIds,
        CancellationToken cancellationToken)
    {
        var ids = dispatchRequestIds.Distinct(StringComparer.Ordinal).ToArray();
        foreach (var batch in ids.Chunk(512))
        {
            var result = await _connector.AcknowledgeDispatchesAsync(batch, cancellationToken).ConfigureAwait(false);
            if (result is not "acknowledged" and not "already_acknowledged" and not "unknown")
                throw new InvalidDataException("dispatch_ack_rejected");
        }
    }

    private async Task ProcessMaintenanceAsync(CancellationToken cancellationToken)
    {
        var capacity = BuildJournalCapacity();
        var command = await _connector.SyncMaintenanceAsync(null, null, null, capacity, cancellationToken)
            .ConfigureAwait(false);
        switch (command.Result)
        {
            case "none":
            case "rejected":
            case "prepared":
            case "committed":
                return;
            case "prepare":
                try
                {
                    PrepareArchive(command);
                    await _connector.SyncMaintenanceAsync(
                        command.BatchId, "prepare", null, BuildJournalCapacity(), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception error) when (error is InvalidDataException or InvalidOperationException)
                {
                    var safeError = SafeError(error.Message);
                    await _connector.SyncMaintenanceAsync(
                        command.BatchId, "prepare", safeError, BuildJournalCapacity(), cancellationToken)
                        .ConfigureAwait(false);
                }
                return;
            case "commit":
                try
                {
                    CommitArchive(command);
                    await _connector.SyncMaintenanceAsync(
                        command.BatchId, "commit", null, BuildJournalCapacity(), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception error) when (error is InvalidDataException or InvalidOperationException)
                {
                    var safeError = SafeError(error.Message);
                    await _connector.SyncMaintenanceAsync(
                        command.BatchId, "commit", safeError, BuildJournalCapacity(), cancellationToken)
                        .ConfigureAwait(false);
                }
                return;
            default:
                throw new InvalidDataException("maintenance_sync_result_invalid");
        }
    }

    private AgentCapacityCounter BuildJournalCapacity() =>
        new("adapter_journal", _records.Count, 512,
            _records.Values.Count(record => record.State == "terminal" && record.PendingEvent is null));

    private void PrepareArchive(AdapterMaintenanceSyncResult command)
    {
        if (command.BatchId is null || command.BatchSha256 is null || command.Items is null)
            throw new InvalidDataException("archive_prepare_payload_invalid");
        if (_archiveState.ActiveBatch is not null)
        {
            if (ArchiveBatchMatches(_archiveState.ActiveBatch, command))
                return;
            throw new InvalidOperationException("archive_batch_conflict");
        }
        if (command.Items.Count is 0 or > 128)
            throw new InvalidDataException("archive_items_invalid");
        foreach (var item in command.Items)
        {
            if (!_records.TryGetValue(item.DispatchRequestId, out var record)
                || !ArchiveItemMatches(record, item))
                throw new InvalidOperationException("archive_record_not_ready");
        }

        var active = new CodexArchiveBatch(command.BatchId, command.BatchSha256,
            CodexArchiveBatchPhase.Prepared, command.Items.ToArray());
        SaveArchiveState(_archiveState with { ActiveBatch = active });
    }

    private void CommitArchive(AdapterMaintenanceSyncResult command)
    {
        if (command.BatchId is null || command.BatchSha256 is null)
            throw new InvalidDataException("archive_commit_payload_invalid");
        var active = _archiveState.ActiveBatch;
        if (active is null
            || active.BatchId != command.BatchId
            || active.BatchSha256 != command.BatchSha256
            || active.Phase != CodexArchiveBatchPhase.Prepared)
        {
            if (active?.BatchId == command.BatchId && active?.Phase == CodexArchiveBatchPhase.Committed)
            {
                RecoverCommittedArchive();
                return;
            }
            throw new InvalidOperationException("archive_batch_not_prepared");
        }

        var tombstones = (_archiveState.Tombstones ?? Array.Empty<AgentArchiveProtocolItem>())
            .ToDictionary(ArchiveIdentityKey, StringComparer.Ordinal);
        foreach (var item in active.Items)
        {
            if (tombstones.TryGetValue(ArchiveIdentityKey(item), out var existing)
                && !existing.Equals(item))
                throw new InvalidOperationException("archive_tombstone_conflict");
            tombstones[ArchiveIdentityKey(item)] = item;
        }
        if (tombstones.Count > 16384)
            throw new InvalidDataException("adapter_archive_tombstones_full");

        // Persist the replay fence before deleting the journal records. A crash
        // after this write is recovered by RecoverCommittedArchive on startup.
        SaveArchiveState(_archiveState with
        {
            ActiveBatch = active with { Phase = CodexArchiveBatchPhase.Committed },
            Tombstones = tombstones.Values.ToArray(),
        });
        RecoverCommittedArchive();
    }

    private void RecoverCommittedArchive()
    {
        var active = _archiveState.ActiveBatch;
        if (active?.Phase != CodexArchiveBatchPhase.Committed) return;
        var covered = active.Items.Select(item => item.DispatchRequestId).ToHashSet(StringComparer.Ordinal);
        var retained = _records.Values.Where(record => !covered.Contains(record.Request.DispatchRequestId)).ToArray();
        if (retained.Length != _records.Count)
        {
            _store.Save(retained);
            _records.Clear();
            foreach (var record in retained)
                _records[record.Request.DispatchRequestId] = record;
        }
        SaveArchiveState(_archiveState with { ActiveBatch = null });
    }

    private void SaveArchiveState(CodexArchiveState state)
    {
        _archiveStore.Save(state);
        _archiveState = state with { Tombstones = state.Tombstones ?? Array.Empty<AgentArchiveProtocolItem>() };
    }

    private bool IsArchivedDispatch(string dispatchRequestId) =>
        (_archiveState.Tombstones ?? Array.Empty<AgentArchiveProtocolItem>())
            .Any(item => item.DispatchRequestId == dispatchRequestId);

    private static bool IsValidArchiveState(CodexArchiveState state) =>
        state.SchemaVersion == 1
        && (state.Tombstones ?? Array.Empty<AgentArchiveProtocolItem>()).Count <= 16384
        && (state.Tombstones ?? Array.Empty<AgentArchiveProtocolItem>()).All(item =>
            item is not null && item.FinalSequence >= 0
            && item.FinalStatus is "completed" or "failed" or "cancelled"
            && !string.IsNullOrWhiteSpace(item.DispatchRequestId));

    private static bool ArchiveBatchMatches(CodexArchiveBatch active, AdapterMaintenanceSyncResult command) =>
        active.BatchId == command.BatchId
        && active.BatchSha256 == command.BatchSha256
        && active.Items.SequenceEqual(command.Items ?? Array.Empty<AgentArchiveProtocolItem>());

    private bool ArchiveItemMatches(CodexDispatchRecord record, AgentArchiveProtocolItem item)
    {
        var terminal = record.TerminalEvent;
        return record.State == "terminal"
            && record.PendingEvent is null
            && terminal is not null
            && record.Request.SourceType == item.SourceType
            && record.Request.SourceInstanceId == item.SourceInstance
            && record.Request.DispatchRequestId == item.DispatchRequestId
            && terminal.TaskId == item.TaskId
            && terminal.DispatchRequestId == item.DispatchRequestId
            && terminal.Sequence == item.FinalSequence
            && terminal.OccurredAt == item.EndedAt
            && TerminalStatus(terminal.EventType) == item.FinalStatus
            && AgentArchiveHashing.CandidateSha256(
                item.SourceType,
                item.SourceInstance,
                item.TaskId,
                item.DispatchRequestId,
                item.FinalSequence,
                item.FinalStatus,
                item.EndedAt) == item.SummarySha256;
    }

    private static string? TerminalStatus(string eventType) => eventType switch
    {
        "task_completed" => "completed",
        "task_failed" => "failed",
        "task_cancelled" => "cancelled",
        _ => null,
    };

    private static string ArchiveIdentityKey(AgentArchiveProtocolItem item) =>
        $"{item.SourceType}\u001f{item.SourceInstance}\u001f{item.TaskId}\u001f{item.DispatchRequestId}";

    private static string SafeError(string value)
    {
        var compact = new string(value.Where(character => !char.IsControl(character)).ToArray()).Trim();
        return compact.Length <= 512 ? compact : compact[..512];
    }

    private void Save(CodexDispatchRecord record)
    {
        var candidate = _records.Values.Where(r => r.Request.DispatchRequestId != record.Request.DispatchRequestId).Append(record).ToArray();
        _store.Save(candidate);
        _records[record.Request.DispatchRequestId] = record;
    }
}
