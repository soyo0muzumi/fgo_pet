using System.Collections.Concurrent;
using FgoPet.Extensibility;

namespace FgoPet.Kernel.Agent;

public sealed record AgentRunResult(AgentRunSnapshot Snapshot, string? FinalText = null,
    AgentWaitState? Waiting = null, bool StatePersisted = true, string? StopMessage = null);
public sealed record AgentInteractionSnapshot(AgentRunSnapshot Snapshot, ApprovalRequest? Approval, UserInputRequest? Input);

/// <summary>Owns admission and the sole writer for each run. Terminal sessions release private model context.</summary>
public sealed class AgentRunCoordinator : IAsyncDisposable
{
    private readonly IAgentRunStore _store;
    private readonly Func<AgentRunIdentity, CancellationToken, ValueTask<IAgentModelStep>> _models;
    private readonly AgentLoop _loop;
    private readonly TimeProvider _time;
    private readonly NativeAgentExtensions? _extensions;
    private readonly IAgentRunFence _fence;
    private int _closed;
    private readonly ConcurrentDictionary<string, string> _admission = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AgentRunSession> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AgentRunSnapshot> _localFailures = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _failureOrder = new();

    public AgentRunCoordinator(IAgentRunStore store,
        Func<AgentRunIdentity, CancellationToken, ValueTask<IAgentModelStep>> models,
        IStepEnvironmentBuilder environment, IToolExecutionPipeline pipeline, IAgentRunFence fence,
        TimeProvider? time = null, NativeAgentExtensions? extensions = null)
        => (_store, _models, _loop, _time, _extensions, _fence)
            = (store, models, new(environment, pipeline, fence, extensions), time ?? TimeProvider.System, extensions, fence);

    public async ValueTask<AgentRunResult> StartAsync(AgentRunRequest request, CancellationToken token)
    {
        AgentRunState.ValidateRequest(request);
        StepEnvironmentBuilder.CheckSize(request.InitialMessages);
        var identity = request.Identity;
        if (Volatile.Read(ref _closed) != 0) throw new AgentStateException("RUN_HOST_CLOSED");
        if (!_admission.TryAdd(identity.Scope.ConversationId, identity.RunId))
            throw new AgentStateException("RUN_ALREADY_ACTIVE");
        AgentRunSession? session = null;
        try
        {
            var state = await AgentRunState.CreateAsync(request, _store, _time, token, _extensions?.Events);
            session = new(state, request.InitialMessages) { Query = request.Query };
            if (!_sessions.TryAdd(identity.RunId, session)) throw new AgentStateException("RUN_ALREADY_ACTIVE");
            if (Volatile.Read(ref _closed) != 0) session.Cancel();
            await session.AdvanceGate.WaitAsync(CancellationToken.None);
            try
            {
                if (state.Snapshot.IsTerminal) return Result(state);
                if (state.IsFenced)
                {
                    var failed = _localFailures.GetValueOrDefault(identity.RunId) ?? state.Snapshot with
                        { Status = AgentRunStatus.Failed, ErrorCode = "RUN_STORE_FAILED", CompletedAt = _time.GetUtcNow() };
                    return new(failed, StatePersisted: false);
                }
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, session.Token);
                try
                {
                    await state.StartAsync(linked.Token);
                    session.Model = await _models(identity, linked.Token);
                    _fence.EnsureCurrent(identity, linked.Token);
                    if (session.Model is IAgentRunPreparation preparation)
                        session.SetPreparedTranscript(await preparation.PrepareAsync(session.Transcript, state, linked.Token));
                    _fence.EnsureCurrent(identity, linked.Token);
                    await _loop.AdvanceAsync(session, linked.Token);
                    return Result(state);
                }
                catch (Exception error) { return await TerminateAsync(session, error); }
            }
            finally { session.AdvanceGate.Release(); }
        }
        finally
        {
            if (session is null || session.State.Snapshot.IsTerminal || session.State.IsFenced)
            {
                if (session is not null) _sessions.TryRemove(new KeyValuePair<string, AgentRunSession>(identity.RunId, session));
                _admission.TryRemove(new KeyValuePair<string, string>(identity.Scope.ConversationId, identity.RunId));
                session?.Dispose();
            }
        }
    }

    public async ValueTask<bool> CancelAsync(string runId, ToolScope scope, CancellationToken token)
    {
        if (!_sessions.TryGetValue(runId, out var session) || session.State.Snapshot.Identity.Scope != scope) return false;
        session.Cancel();
        // Once cancellation is accepted, cleanup must finish even if its transport request is abandoned.
        await session.AdvanceGate.WaitAsync(CancellationToken.None);
        try
        {
            if (!session.State.Snapshot.IsTerminal && !session.State.IsFenced)
                await TerminateAsync(session, new OperationCanceledException());
            return true;
        }
        finally
        {
            session.AdvanceGate.Release();
            _sessions.TryRemove(runId, out _);
            _admission.TryRemove(new KeyValuePair<string, string>(scope.ConversationId, runId));
            session.Dispose();
        }
    }

    public async ValueTask<AgentRunSnapshot?> GetSnapshotAsync(string runId, ToolScope scope, CancellationToken token)
    {
        var snapshot = _localFailures.TryGetValue(runId, out var local) ? local
            : (await _store.LoadAsync(runId, token))?.Snapshot;
        return snapshot?.Identity.Scope == scope ? snapshot : null;
    }

    public ValueTask<AgentInteractionSnapshot?> GetInteractionAsync(string runId, ToolScope scope, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!_sessions.TryGetValue(runId, out var session) || session.State.Snapshot.Identity.Scope != scope)
            return ValueTask.FromResult<AgentInteractionSnapshot?>(null);
        _fence.EnsureCurrent(session.State.Snapshot.Identity, token);
        var checkpoint = session.State.Checkpoint;
        return ValueTask.FromResult<AgentInteractionSnapshot?>(checkpoint.Waiting is null ? null
            : new(checkpoint.Snapshot, checkpoint.PendingApproval, checkpoint.PendingInput));
    }

    /// <summary>The caller supplies the current host-owned scope. Model text and answers never grant approval.</summary>
    public ValueTask<AgentRunResult> ResumeInputAsync(UserInputReply reply, ToolScope scope, CancellationToken token)
        => ResumeAsync(reply.RunId, scope, session =>
        {
            var request = session.State.Checkpoint.PendingInput;
            if (request is null || _extensions?.Questions is null) throw new AgentStateException("RUN_INTERACTION_REJECTED");
            var result = _extensions.Questions.Validate(request, reply, scope);
            return cancellation => session.State.ResolveInputAsync(reply, result, cancellation);
        }, token);

    public ValueTask<AgentRunResult> ResumeApprovalAsync(ApprovalReply reply, ToolScope scope, CancellationToken token)
        => ResumeAsync(reply.RunId, scope, session =>
        {
            var request = session.State.Checkpoint.PendingApproval;
            if (request is null || _extensions?.Approvals is null) throw new AgentStateException("RUN_INTERACTION_REJECTED");
            _extensions.Approvals.Validate(request, reply, scope);
            return cancellation => session.State.ResolveApprovalAsync(reply, cancellation);
        }, token);

    public ValueTask<AgentRunResult> ExpireInteractionAsync(string runId, ToolScope scope, CancellationToken token)
        => ResumeAsync(runId, scope, session =>
        {
            if (session.State.Checkpoint.Waiting is null || session.State.Checkpoint.Waiting.ExpiresAt > _time.GetUtcNow())
                throw new AgentStateException("RUN_INTERACTION_NOT_EXPIRED");
            return cancellation => session.State.ExpireWaitAsync(cancellation);
        }, token);

    private async ValueTask<AgentRunResult> ResumeAsync(string runId, ToolScope scope,
        Func<AgentRunSession, Func<CancellationToken, ValueTask>> validate, CancellationToken token)
    {
        if (!_sessions.TryGetValue(runId, out var session) || session.State.Snapshot.Identity.Scope != scope)
            throw new AgentStateException("RUN_INTERACTION_REJECTED");
        await session.AdvanceGate.WaitAsync(token);
        try
        {
            if (session.State.IsFenced || session.State.Snapshot.IsTerminal || session.State.Checkpoint.Waiting is null)
                throw new AgentStateException("RUN_INTERACTION_REJECTED");
            // Invalid or stale replies leave the live card untouched. Validation precedes all state changes.
            var consume = validate(session);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, session.Token);
            try
            {
                _fence.EnsureCurrent(session.State.Snapshot.Identity, linked.Token);
                var index = session.State.Checkpoint.NextCallIndex;
                var callId = session.State.CurrentCall().Call.CallId;
                await consume(linked.Token);
                if (session.State.Checkpoint.NextCallIndex != index)
                {
                    AgentLoop.AppendObservation(session, callId, session.State.Checkpoint.Calls[index].Result!);
                    if (session.State.Checkpoint.NextCallIndex == session.State.Checkpoint.Calls.Length)
                        await session.State.StepCompletedAsync(linked.Token);
                }
                await _loop.AdvanceAsync(session, linked.Token);
                return Result(session.State);
            }
            catch (Exception error) { return await TerminateAsync(session, error); }
        }
        finally
        {
            session.AdvanceGate.Release();
            if (session.State.Snapshot.IsTerminal || session.State.IsFenced)
            {
                _sessions.TryRemove(new KeyValuePair<string, AgentRunSession>(runId, session));
                _admission.TryRemove(new KeyValuePair<string, string>(scope.ConversationId, runId));
                session.Dispose();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _closed, 1);
        var sessions = _sessions.ToArray();
        foreach (var pair in sessions) pair.Value.Cancel();
        foreach (var pair in sessions)
            await CancelAsync(pair.Key, pair.Value.State.Snapshot.Identity.Scope, CancellationToken.None);
    }

    private async ValueTask<AgentRunResult> TerminateAsync(AgentRunSession session, Exception error)
    {
        var state = session.State;
        var unknown = state.Checkpoint.Calls.Any(call => call.Effect == ToolEffect.Command
            && call.Status is AgentCallStatus.Started or AgentCallStatus.ExecutionUnknown);
        var status = unknown ? AgentRunStatus.ExecutionUnknown : error switch
        {
            AgentBudgetExceededException => AgentRunStatus.BudgetExceeded,
            OperationCanceledException => AgentRunStatus.Cancelled,
            _ => AgentRunStatus.Failed
        };
        var code = unknown ? "TOOL_EXECUTION_UNKNOWN" : error switch
        {
            AgentBudgetExceededException => "RUN_BUDGET_EXCEEDED",
            OperationCanceledException => "RUN_CANCELLED",
            AgentProtocolException protocol => protocol.Code,
            AgentStateException operation => operation.Code,
            _ => "RUN_OPERATION_FAILED"
        };
        if (state.Snapshot.IsTerminal) return Result(state);
        if (!state.IsFenced)
        {
            try
            {
                await state.FinishAsync(status, code, CancellationToken.None);
                return Result(state);
            }
            catch (AgentStateException) { }
        }
        var local = state.Snapshot with { Status = status, ErrorCode = AgentRunState.SafeCode(code), CompletedAt = _time.GetUtcNow() };
        _localFailures[local.Identity.RunId] = local;
        _failureOrder.Enqueue(local.Identity.RunId);
        while (_localFailures.Count > 256 && _failureOrder.TryDequeue(out var old)) _localFailures.TryRemove(old, out _);
        return new(local, StatePersisted: false, StopMessage: StopMessage(status));
    }

    private static AgentRunResult Result(AgentRunState state) => new(state.Snapshot, state.Checkpoint.FinalText,
        state.Checkpoint.Waiting, StopMessage: StopMessage(state.Snapshot.Status));
    private static string? StopMessage(AgentRunStatus status) => status == AgentRunStatus.BudgetExceeded
        ? "本次运行已达到执行预算，请发起新请求继续。" : null;
}
