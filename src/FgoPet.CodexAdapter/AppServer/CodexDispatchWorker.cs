using FgoPet.AgentProtocol.Messages;
using FgoPet.AgentRuntime.Security;
using FgoPet.AgentRuntime.Storage;
using FgoPet.CodexAdapter.Relay;

namespace FgoPet.CodexAdapter.AppServer;

public sealed record CodexDispatchRecord(DispatchTaskRequest Request, long Sequence, string State,
    string? ThreadId = null, AgentEventMessage? PendingEvent = null, AgentEventMessage? TerminalEvent = null);

/// <summary>Durable dedupe and terminal outbox. Interrupted work is reported, never silently restarted.</summary>
public sealed class CodexDispatchWorker
{
    private readonly ICodexRelayConnector _connector;
    private readonly ICodexTaskExecutor _executor;
    private readonly AtomicProtectedJsonStore<CodexDispatchRecord[]> _store;
    private readonly Dictionary<string, CodexDispatchRecord> _records;

    public CodexDispatchWorker(ICodexRelayConnector connector, ICodexTaskExecutor executor, string stateRoot, ISecretProtector? protector = null)
    {
        _connector = connector;
        _executor = executor;
        var identityKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(connector.SourceInstanceId)));
        _store = new(Path.Combine(stateRoot, "CodexAdapter", "dispatches-" + identityKey + ".v1.json"),
            protector ?? new DpapiSecretProtector());
        _records = _store.Load(() => [], records => records.Length <= 512 && records.All(r => r.Request is not null
            && r.Request.SourceInstanceId == connector.SourceInstanceId && r.Request.SourceType == "codex" && r.Sequence >= 0)
            && records.Select(r => r.Request.DispatchRequestId).Distinct().Count() == records.Length)
            .ToDictionary(r => r.Request.DispatchRequestId, StringComparer.Ordinal);
        foreach (var old in _records.Values.Where(r => r.State != "terminal").ToArray())
            SetEvent(old, "task_cancelled", "adapter_interrupted", terminal: true);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var connection = await _connector.EnsureAuthenticatedAsync(cancellationToken).ConfigureAwait(false);
                if (connection.Status is AdapterConnectionStatus.Revoked or AdapterConnectionStatus.Rejected or AdapterConnectionStatus.VersionMismatch) return;
                if (connection.Status == AdapterConnectionStatus.Connected)
                {
                    foreach (var record in _records.Values.Where(r => r.PendingEvent is not null).ToArray())
                        await DeliverAsync(record, cancellationToken).ConfigureAwait(false);
                    var requests = await _connector.PollDispatchesAsync(cancellationToken).ConfigureAwait(false);
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
                        if (_records.Count >= 512)
                        {
                            journalFull = true;
                            continue;
                        }
                        Save(new(request, 0, "queued"));
                        acknowledgedIds.Add(request.DispatchRequestId);
                    }
                    if (acknowledgedIds.Count > 0)
                        await AcknowledgeDispatchesAsync(acknowledgedIds, cancellationToken).ConfigureAwait(false);
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
            }
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ExecuteAsync(CodexDispatchRecord record, CancellationToken cancellationToken)
    {
        var request = record.Request;
        if (!await _connector.IsDispatchAllowedAsync(request.TargetId, cancellationToken).ConfigureAwait(false))
        {
            SetEvent(record, "task_cancelled", "target_permission_removed", true);
            return;
        }
        Save(record with { State = "running" }); // Persist before side effects, including process/thread creation.
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
        { result = "task_failed"; }
        finally
        {
            await execution.CancelAsync().ConfigureAwait(false);
            try { await watching.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
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
            DispatchRequestId: record.Request.DispatchRequestId);
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
        await _connector.SendEventAsync(record.PendingEvent, cancellationToken).ConfigureAwait(false);
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

    private void Save(CodexDispatchRecord record)
    {
        var candidate = _records.Values.Where(r => r.Request.DispatchRequestId != record.Request.DispatchRequestId).Append(record).ToArray();
        _store.Save(candidate);
        _records[record.Request.DispatchRequestId] = record;
    }
}
