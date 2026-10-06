using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using FgoPet.Extensibility;

namespace FgoPet.Kernel.Agent;

/// <summary>Coordinator-owned writer. Counters, intent, cursor and metadata journal share a revision.</summary>
internal sealed class AgentRunState : IModelRequestBudget, IToolExecutionIntent
{
    private readonly IAgentRunStore _store;
    private readonly TimeProvider _time;
    private readonly AgentEventDispatcher? _events;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _acceptedCallIds = new(StringComparer.Ordinal);
    private AgentRunCheckpoint _checkpoint;
    public bool IsFenced { get; private set; }
    public AgentRunCheckpoint Checkpoint => Volatile.Read(ref _checkpoint);
    public AgentRunSnapshot Snapshot => Checkpoint.Snapshot;

    private AgentRunState(IAgentRunStore store, TimeProvider time, AgentRunCheckpoint initial, AgentEventDispatcher? events)
        => (_store, _time, _checkpoint, _events) = (store, time, initial, events);

    public static async ValueTask<AgentRunState> CreateAsync(AgentRunRequest request,
        IAgentRunStore store, TimeProvider time, CancellationToken token, AgentEventDispatcher? dispatcher = null)
    {
        ValidateRequest(request);
        var snapshot = new AgentRunSnapshot { Identity = request.Identity, Budget = request.Budget,
            Status = AgentRunStatus.Created, StartedAt = time.GetUtcNow() };
        var initial = new AgentRunCheckpoint { Snapshot = snapshot, JournalSequence = 1 };
        var events = ImmutableArray.Create(new AgentEvent(1, time.GetUtcNow(), AgentEventKind.RunCreated,
            Correlation(request.Identity.RunId), 0));
        try
        {
            if (!await store.TryCreateAsync(initial, events, token)) throw new AgentStateException("RUN_CREATE_REJECTED");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (AgentStateException) { throw; }
        catch (Exception) { throw new AgentStateException("RUN_STORE_FAILED"); }
        var state = new AgentRunState(store, time, initial, dispatcher);
        foreach (var call in request.InitialMessages.SelectMany(message => message.ToolCalls))
            state._acceptedCallIds.Add(call.CallId);
        await state.NotifyAsync(events);
        return state;
    }

    internal static void ValidateRequest(AgentRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var id = request.Identity;
        if (id is null || id.Scope is null || request.Budget is null || !ValidId(id.RunId)
            || !ValidId(id.RootUserMessageId) || !ValidId(id.Scope.ConversationId) || !ValidId(id.Scope.RoleId)
            || id.Scope.ProjectId is not null && !ValidId(id.Scope.ProjectId)
            || !ValidId(id.ModelRevision) || id.AuthorizationRevision < 0)
            throw new AgentStateException("RUN_INVALID_SCOPE");
        ModelProtocol.ValidateTranscript(request.InitialMessages);
        if (request.Query is not null && (request.Query.UserMessageId != id.RootUserMessageId
            || request.Query.Fingerprint is not { Length: 64 }
            || !request.Query.Fingerprint.All(c => c is >= 'A' and <= 'F' or >= '0' and <= '9')))
            throw new AgentStateException("RUN_INVALID_QUERY_REFERENCE");
    }

    public ValueTask StartAsync(CancellationToken token) => CommitAsync(current =>
    {
        if (current.Snapshot.Status != AgentRunStatus.Created) throw new AgentStateException("RUN_ALREADY_STARTED");
        return current with { Snapshot = current.Snapshot with { Status = AgentRunStatus.Running } };
    }, [AgentEventKind.RunStarted], token);

    public ValueTask BeginStepAsync(CancellationToken token) => CommitAsync(current =>
    {
        EnsureRunning(current);
        if (current.NextCallIndex != current.Calls.Length) throw new AgentStateException("RUN_PENDING_CALLS");
        return current with { Calls = [], NextCallIndex = 0,
            CompletedSteps = current.Calls.IsEmpty ? current.CompletedSteps : current.CompletedSteps.Add(
                new AgentCompletedStep(current.Snapshot.StepNumber, current.Calls)),
            Snapshot = current.Snapshot with { StepNumber = current.Snapshot.StepNumber + 1 } };
    }, [AgentEventKind.StepStarted], token);

    public ValueTask ReserveAsync(CancellationToken token) => CommitAsync(current =>
    {
        EnsureRunning(current);
        if (current.Snapshot.ModelRequests >= current.Snapshot.Budget.MaxModelRequests)
            throw new AgentBudgetExceededException("model_requests");
        return current with { Snapshot = current.Snapshot with { ModelRequests = current.Snapshot.ModelRequests + 1 } };
    }, [AgentEventKind.ModelStarted], token);

    public async ValueTask AcceptResponseAsync(ModelStepResponse response, CancellationToken token)
    {
        ModelProtocol.ValidateResponse(response, _acceptedCallIds);
        await CommitAsync(current =>
        {
            EnsureRunning(current);
            if (current.NextCallIndex != current.Calls.Length) throw new AgentStateException("RUN_PENDING_CALLS");
            return current with { Calls = response.ToolCalls.Select(call => new AgentCallCheckpoint(call)).ToImmutableArray(),
                NextCallIndex = 0 };
        }, [AgentEventKind.ModelCompleted], token);
        foreach (var call in response.ToolCalls) _acceptedCallIds.Add(call.CallId);
    }

    public ValueTask ReserveToolAsync(CancellationToken token)
    {
        if (IsFenced || Snapshot.Status != AgentRunStatus.Running) throw new AgentStateException("RUN_CLOSED");
        var currentCall = CurrentCall();
        if (currentCall.Charged) return ValueTask.CompletedTask;
        return CommitAsync(current =>
        {
            EnsureRunning(current);
            var call = current.Calls[current.NextCallIndex];
            if (call.Charged) throw new AgentStateException("RUN_CONCURRENT_RESERVE");
            if (current.Snapshot.ToolCalls >= current.Snapshot.Budget.MaxToolCalls)
                throw new AgentBudgetExceededException("tool_calls");
            return current with { Calls = current.Calls.SetItem(current.NextCallIndex, call with { Charged = true }),
                Snapshot = current.Snapshot with { ToolCalls = current.Snapshot.ToolCalls + 1 } };
        }, [AgentEventKind.ToolRequested], token, currentCall.Call.CallId);
    }

    public ValueTask CommitStartedAsync(ToolDescriptor descriptor, CancellationToken token) => CommitAsync(current =>
    {
        EnsureRunning(current);
        var call = current.Calls[current.NextCallIndex];
        if (!call.Charged || call.Status != AgentCallStatus.Requested) throw new AgentStateException("RUN_INVALID_TOOL_INTENT");
        return current with { Calls = current.Calls.SetItem(current.NextCallIndex,
            call with { Status = AgentCallStatus.Started, Effect = descriptor.Effect }) };
    }, [AgentEventKind.ToolStarted], token, CurrentCall().Call.CallId);

    public ValueTask CompleteToolAsync(ToolExecutionOutcome outcome, CancellationToken token)
    {
        var callId = CurrentCall().Call.CallId;
        if (outcome.Kind is ToolExecutionOutcomeKind.WaitingApproval or ToolExecutionOutcomeKind.WaitingUserInput)
        {
            if (outcome.Waiting is null || outcome.Waiting.CallId != callId
                || outcome.Waiting.StepNumber != Snapshot.StepNumber)
                throw new AgentStateException("RUN_INVALID_WAIT");
            return CommitAsync(current =>
            {
                EnsureRunning(current);
                var call = current.Calls[current.NextCallIndex];
                if (!call.Charged || call.Status != AgentCallStatus.Requested) throw new AgentStateException("RUN_INVALID_WAIT");
                var waiting = outcome.Waiting with { Revision = current.Snapshot.Revision + 1 };
                var expectedKind = outcome.Kind == ToolExecutionOutcomeKind.WaitingApproval ? AgentWaitKind.Approval : AgentWaitKind.UserInput;
                if (waiting.Kind != expectedKind) throw new AgentStateException("RUN_INVALID_WAIT");
                var binding = outcome.ApprovalRequest?.Binding ?? outcome.UserInputRequest?.Binding;
                if (binding is not null && (binding.Identity != current.Snapshot.Identity || binding.CallId != callId
                    || binding.StepNumber != current.Snapshot.StepNumber || binding.RequestId != waiting.RequestId
                    || binding.WaitingRevision != waiting.Revision || binding.ExpiresAt != waiting.ExpiresAt))
                    throw new AgentStateException("RUN_INVALID_WAIT");
                return current with { Waiting = waiting, PendingApproval = outcome.ApprovalRequest, PendingInput = outcome.UserInputRequest,
                    Snapshot = current.Snapshot with { Status = expectedKind == AgentWaitKind.Approval
                        ? AgentRunStatus.WaitingApproval : AgentRunStatus.WaitingUserInput } };
            },
                [outcome.Kind == ToolExecutionOutcomeKind.WaitingApproval ? AgentEventKind.ApprovalRequested
                    : AgentEventKind.UserInputRequested], token, callId);
        }
        if (outcome.Kind == ToolExecutionOutcomeKind.Cancelled) throw new OperationCanceledException(token);
        if (outcome.Kind == ToolExecutionOutcomeKind.Completed && outcome.Result is null)
            throw new AgentStateException("RUN_MISSING_TOOL_RESULT");
        var status = outcome.Kind == ToolExecutionOutcomeKind.ExecutionUnknown ? AgentCallStatus.ExecutionUnknown
            : outcome.Result!.Success ? AgentCallStatus.Completed
            : outcome.Result.ErrorCode == "TOOL_AUTHORIZATION_DENIED" ? AgentCallStatus.Denied : AgentCallStatus.Failed;
        return CommitAsync(current =>
        {
            EnsureRunning(current);
            if (!current.Calls[current.NextCallIndex].Charged) throw new AgentStateException("RUN_UNCHARGED_TOOL");
            return current with { Calls = current.Calls.SetItem(current.NextCallIndex,
            current.Calls[current.NextCallIndex] with { Status = status, Result = outcome.Result }),
                NextCallIndex = current.NextCallIndex + 1 };
        }, [status == AgentCallStatus.Completed ? AgentEventKind.ToolCompleted : AgentEventKind.ToolFailed], token, callId,
            outcome.ErrorCode ?? outcome.Result?.ErrorCode);
    }

    public ValueTask CompleteAsync(string finalText, CancellationToken token) => CommitAsync(current =>
    {
        EnsureRunning(current);
        if (current.NextCallIndex != current.Calls.Length || string.IsNullOrWhiteSpace(finalText) || finalText.Length > 12000)
            throw new AgentStateException("RUN_INVALID_FINAL");
        return current with { FinalText = finalText, FinalDeliveryId = "native-final-" + Correlation(current.Snapshot.Identity.RunId),
            Waiting = null, Snapshot = current.Snapshot with { Status = AgentRunStatus.Completed, CompletedAt = _time.GetUtcNow() } };
    }, [AgentEventKind.StepCompleted, AgentEventKind.RunCompleted], token);

    public ValueTask StepCompletedAsync(CancellationToken token) => CommitAsync(current =>
    {
        if (current.NextCallIndex != current.Calls.Length) throw new AgentStateException("RUN_PENDING_CALLS");
        return current;
    }, [AgentEventKind.StepCompleted], token);

    public void EnsureSkillCapacity(string id)
    {
        if (IsFenced) throw new AgentStateException("RUN_CLOSED");
        EnsureRunning(Checkpoint);
        if (!Checkpoint.ActiveSkills.Any(skill => skill.Id == id)
            && Snapshot.LoadedSkills >= Snapshot.Budget.MaxLoadedSkills)
            throw new AgentBudgetExceededException("loaded_skills");
    }

    public ValueTask ActivateSkillAsync(ActiveSkillMetadata skill, CancellationToken token)
    {
        EnsureSkillCapacity(skill.Id);
        var previous = Checkpoint.ActiveSkills.FirstOrDefault(active => active.Id == skill.Id);
        if (previous is not null)
        {
            if (previous != skill) throw new AgentStateException("SKILL_CONTENT_CHANGED");
            return ValueTask.CompletedTask;
        }
        return CommitAsync(current =>
        {
            EnsureRunning(current);
            if (!ValidId(skill.Id) || !ValidId(skill.PluginId) || !Version.TryParse(skill.Version, out _)
                || skill.ContentDigest is not { Length: 64 }
                || !skill.ContentDigest.All(c => c is >= 'A' and <= 'F' or >= '0' and <= '9'))
                throw new AgentStateException("SKILL_INVALID_METADATA");
            if (current.ActiveSkills.Any(active => active.Id == skill.Id)) throw new AgentStateException("RUN_CONCURRENT_ACTIVATION");
            if (current.Snapshot.LoadedSkills >= current.Snapshot.Budget.MaxLoadedSkills)
                throw new AgentBudgetExceededException("loaded_skills");
            return current with { ActiveSkills = current.ActiveSkills.Add(skill),
                Snapshot = current.Snapshot with { LoadedSkills = current.Snapshot.LoadedSkills + 1 } };
        }, [AgentEventKind.SkillLoaded], token);
    }

    public ValueTask ResolveInputAsync(UserInputReply reply, ToolResult result, CancellationToken token)
        => CommitAsync(current =>
        {
            CheckWaiting(current, reply.RequestId, reply.ExpectedRevision, AgentWaitKind.UserInput);
            if (current.PendingInput is null || reply.RunId != current.Snapshot.Identity.RunId)
                throw new AgentStateException("RUN_INTERACTION_REJECTED");
            var next = ResolveCall(current, result);
            return next with { ResolvedInputs = current.ResolvedInputs.Add(new(current.PendingInput, reply)) };
        }, [AgentEventKind.UserInputResolved, AgentEventKind.ToolCompleted], token, CurrentCall().Call.CallId);

    public ValueTask ResolveApprovalAsync(ApprovalReply reply, CancellationToken token)
        => CommitAsync(current =>
        {
            CheckWaiting(current, reply.RequestId, reply.ExpectedRevision, AgentWaitKind.Approval);
            if (current.PendingApproval is null || reply.RunId != current.Snapshot.Identity.RunId || !Enum.IsDefined(reply.Decision))
                throw new AgentStateException("RUN_INTERACTION_REJECTED");
            if (reply.Decision == ApprovalDecision.Deny)
                return ResolveCall(current, ToolResultNormalizer.Failure("TOOL_AUTHORIZATION_DENIED").Result!);
            return current with { Calls = current.Calls.SetItem(current.NextCallIndex,
                current.Calls[current.NextCallIndex] with { Approval = current.PendingApproval }),
                Waiting = null, PendingApproval = null, PendingInput = null,
                Snapshot = current.Snapshot with { Status = AgentRunStatus.Running } };
        }, reply.Decision == ApprovalDecision.Allow ? [AgentEventKind.ApprovalResolved]
            : [AgentEventKind.ApprovalResolved, AgentEventKind.ToolFailed], token, CurrentCall().Call.CallId,
            reply.Decision == ApprovalDecision.Deny ? "TOOL_AUTHORIZATION_DENIED" : null);

    public ValueTask ExpireWaitAsync(CancellationToken token)
        => CommitAsync(current =>
        {
            if (current.Waiting is null || current.Waiting.ExpiresAt > _time.GetUtcNow())
                throw new AgentStateException("RUN_INTERACTION_NOT_EXPIRED");
            CheckWaiting(current, current.Waiting.RequestId, current.Snapshot.Revision, current.Waiting.Kind, allowExpired: true);
            return ResolveCall(current, ToolResultNormalizer.Failure(current.Waiting.Kind == AgentWaitKind.Approval
                ? "TOOL_APPROVAL_EXPIRED" : "TOOL_USER_INPUT_EXPIRED").Result!);
        }, [AgentEventKind.ToolFailed], token, CurrentCall().Call.CallId, "RUN_INTERACTION_EXPIRED");

    private void CheckWaiting(AgentRunCheckpoint current, string requestId, long revision, AgentWaitKind kind, bool allowExpired = false)
    {
        if (current.Waiting is null || current.Waiting.RequestId != requestId || current.Waiting.Revision != revision
            || current.Snapshot.Revision != revision || current.Waiting.Kind != kind
            || current.Snapshot.Status != (kind == AgentWaitKind.Approval ? AgentRunStatus.WaitingApproval : AgentRunStatus.WaitingUserInput)
            || current.Waiting.StepNumber != current.Snapshot.StepNumber || current.NextCallIndex >= current.Calls.Length
            || current.Waiting.CallId != current.Calls[current.NextCallIndex].Call.CallId)
            throw new AgentStateException("RUN_INTERACTION_REJECTED");
        if (!allowExpired && current.Waiting.ExpiresAt <= _time.GetUtcNow()) throw new AgentStateException("RUN_INTERACTION_EXPIRED");
    }

    private static AgentRunCheckpoint ResolveCall(AgentRunCheckpoint current, ToolResult result)
        => current with { Calls = current.Calls.SetItem(current.NextCallIndex, current.Calls[current.NextCallIndex] with
                { Result = result, Status = result.Success ? AgentCallStatus.Completed
                    : result.ErrorCode == "TOOL_AUTHORIZATION_DENIED" ? AgentCallStatus.Denied : AgentCallStatus.Failed }),
            NextCallIndex = current.NextCallIndex + 1, Waiting = null, PendingApproval = null, PendingInput = null,
            Snapshot = current.Snapshot with { Status = AgentRunStatus.Running } };

    public ValueTask FinishAsync(AgentRunStatus status, string code, CancellationToken token) => CommitAsync(current =>
    {
        if (status is not (AgentRunStatus.Failed or AgentRunStatus.Cancelled or AgentRunStatus.BudgetExceeded
            or AgentRunStatus.Interrupted or AgentRunStatus.ExecutionUnknown)) throw new AgentStateException("RUN_INVALID_TERMINAL");
        return current with { Waiting = null, PendingInput = null, PendingApproval = null,
            Calls = current.Calls.Select(call => call.Status == AgentCallStatus.Requested
            ? call with { Status = AgentCallStatus.NotExecuted } : call).ToImmutableArray(),
            Snapshot = current.Snapshot with { Status = status, ErrorCode = SafeCode(code), CompletedAt = _time.GetUtcNow() } };
    },
        [TerminalEvent(status)], token);

    public AgentCallCheckpoint CurrentCall()
    {
        var current = Checkpoint;
        if (current.NextCallIndex >= current.Calls.Length) throw new AgentStateException("RUN_NO_CURRENT_CALL");
        return current.Calls[current.NextCallIndex];
    }

    private async ValueTask CommitAsync(Func<AgentRunCheckpoint, AgentRunCheckpoint> change,
        ImmutableArray<AgentEventKind> kinds, CancellationToken token, string? callId = null, string? eventCode = null)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (IsFenced || Snapshot.IsTerminal) throw new AgentStateException("RUN_CLOSED");
            var current = Checkpoint;
            var next = change(current);
            if (!LegalTransition(current.Snapshot.Status, next.Snapshot.Status)) throw new AgentStateException("RUN_INVALID_TRANSITION");
            var events = kinds.Select((kind, index) => new AgentEvent(current.JournalSequence + index + 1,
                _time.GetUtcNow(), kind, Correlation(current.Snapshot.Identity.RunId), next.Snapshot.StepNumber,
                callId is null ? null : Correlation(callId), SafeCode(eventCode ?? next.Snapshot.ErrorCode))).ToImmutableArray();
            next = next with { Snapshot = next.Snapshot with { Revision = current.Snapshot.Revision + 1 },
                JournalSequence = current.JournalSequence + events.Length };
            try
            {
                if (!await _store.TryCommitAsync(next, current.Snapshot.Revision, events, token))
                    throw new AgentStateException("RUN_REVISION_CONFLICT");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Cancellation cannot prove whether the store published its transaction.
                IsFenced = true;
                throw;
            }
            catch (Exception)
            {
                IsFenced = true;
                throw new AgentStateException("RUN_STORE_FAILED");
            }
            Volatile.Write(ref _checkpoint, next);
            await NotifyAsync(events);
        }
        finally { _gate.Release(); }
    }

    private async ValueTask NotifyAsync(ImmutableArray<AgentEvent> events)
    {
        if (_events is null) return;
        try { await _events.DispatchAsync(events, CancellationToken.None); }
        catch (Exception) { /* A bounded metadata notification cannot invalidate a committed transaction. */ }
    }

    private static void EnsureRunning(AgentRunCheckpoint current)
    {
        if (current.Snapshot.Status != AgentRunStatus.Running) throw new AgentStateException("RUN_NOT_RUNNING");
    }
    private static bool LegalTransition(AgentRunStatus from, AgentRunStatus to) => from == to
        || from == AgentRunStatus.Created && to is AgentRunStatus.Running or AgentRunStatus.Failed or AgentRunStatus.Cancelled
        || from == AgentRunStatus.Running && to != AgentRunStatus.Created
        || from is AgentRunStatus.WaitingApproval or AgentRunStatus.WaitingUserInput && to != AgentRunStatus.Created;
    private static AgentEventKind TerminalEvent(AgentRunStatus status) => status switch
    {
        AgentRunStatus.Cancelled => AgentEventKind.RunCancelled,
        AgentRunStatus.BudgetExceeded => AgentEventKind.RunBudgetExceeded,
        AgentRunStatus.Interrupted => AgentEventKind.RunInterrupted,
        AgentRunStatus.ExecutionUnknown => AgentEventKind.RunExecutionUnknown,
        _ => AgentEventKind.RunFailed
    };
    private static bool ValidId(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128
        && !value.Any(char.IsControl);
    internal static string Correlation(string id) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)))[..24];
    internal static string? SafeCode(string? code) => code is null ? null
        : code.Length is > 0 and <= 64 && code.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_')
            ? code : "RUN_OPERATION_FAILED";
}
