using System.Collections.Concurrent;
using FgoPet.Core.Dialogue;
using FgoPet.Extensibility;
using FgoPet.Kernel.Agent;

namespace FgoPet.App.Dialogue;

/// <summary>One host admission owner; each run retains its trusted scope and generation fence across waits.</summary>
public sealed class NativeConversationRuntime : IAsyncDisposable, IDisposable
{
    private sealed record Registration(AgentRunIdentity Identity, Action CheckCurrent,
        Func<CancellationToken, ValueTask<IAgentModelStep>> Model,
        Func<AgentRunResult, CancellationToken, ValueTask<ConversationSendResult>> Deliver);
    private readonly ConcurrentDictionary<string, Registration> registrations = new(StringComparer.Ordinal);
    private readonly AgentRunCoordinator coordinator;
    private readonly Fence fence;
    private readonly Func<bool> admissionAvailable;
    private int closed;
    private readonly ConcurrentDictionary<string, Task> cancellations = new(StringComparer.Ordinal);

    public NativeConversationRuntime(IAgentRunStore store, ToolRegistry tools, NativeAgentExtensions extensions,
        TimeProvider? time = null, IToolPolicy? policy = null, Func<bool>? admissionAvailable = null)
    {
        this.admissionAvailable = admissionAvailable ?? (() => true);
        fence = new(registrations);
        coordinator = new(store, (identity, token) => Get(identity).Model(token),
            new StepEnvironmentBuilder(tools.GetTools), new ScopedPipeline(tools, fence, policy, extensions.Approvals),
            fence, time, extensions);
    }
    public async ValueTask<ConversationSendResult> StartAsync(AgentRunRequest request,
        Func<CancellationToken, ValueTask<IAgentModelStep>> model, Action checkCurrent,
        Func<AgentRunResult, CancellationToken, ValueTask<ConversationSendResult>> deliver, CancellationToken token)
    {
        if (Volatile.Read(ref closed) != 0 || !admissionAvailable()) throw new AgentStateException("RUN_HOST_CLOSED");
        var registration = new Registration(request.Identity, checkCurrent, model, deliver);
        if (!registrations.TryAdd(request.Identity.RunId, registration)) throw new AgentStateException("RUN_ALREADY_ACTIVE");
        try { return await Finish(await coordinator.StartAsync(request, token), token); }
        catch { registrations.TryRemove(request.Identity.RunId, out _); throw; }
    }
    public async ValueTask<ConversationSendResult> ResumeInputAsync(UserInputReply reply, ToolScope scope, CancellationToken token) =>
        await Finish(await coordinator.ResumeInputAsync(reply, scope, token), token);
    public async ValueTask<ConversationSendResult> ResumeApprovalAsync(ApprovalReply reply, ToolScope scope, CancellationToken token) =>
        await Finish(await coordinator.ResumeApprovalAsync(reply, scope, token), token);
    public async ValueTask<ConversationSendResult> ExpireAsync(string runId, ToolScope scope, CancellationToken token) =>
        await Finish(await coordinator.ExpireInteractionAsync(runId, scope, token), token);
    public ValueTask<AgentInteractionSnapshot?> GetInteractionAsync(string runId, ToolScope scope, CancellationToken token) =>
        coordinator.GetInteractionAsync(runId, scope, token);
    public ValueTask<AgentRunSnapshot?> GetSnapshotAsync(string runId, ToolScope scope, CancellationToken token) =>
        coordinator.GetSnapshotAsync(runId, scope, token);
    public async ValueTask<bool> CancelAsync(string runId, ToolScope scope, CancellationToken token)
    {
        var cancelled = await coordinator.CancelAsync(runId, scope, token);
        if (cancelled) registrations.TryRemove(runId, out _);
        return cancelled;
    }
    public bool HasActiveConversation(string conversationId) => registrations.Values.Any(r => r.Identity.Scope.ConversationId == conversationId);
    public void CancelConversation(string conversationId)
    {
        foreach (var registration in registrations.Values.Where(r => r.Identity.Scope.ConversationId == conversationId))
            BeginCancel(registration.Identity);
    }
    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref closed, 1);
        await coordinator.DisposeAsync();
        await Task.WhenAll(cancellations.Values);
        registrations.Clear();
    }
    // Synchronous container teardown fences admission and signals cancellation without blocking a UI dispatcher.
    // The lifecycle's asynchronous StopAsync owns the awaited drain during normal application shutdown.
    public void Dispose()
    {
        Interlocked.Exchange(ref closed, 1);
        CancelAll();
    }
    /// <summary>Cancel active and waiting sessions immediately; owned cleanup is awaited on shutdown.</summary>
    public void CancelAll()
    {
        foreach (var registration in registrations.Values) BeginCancel(registration.Identity);
    }
    private void BeginCancel(AgentRunIdentity identity)
    {
        foreach (var pair in cancellations.Where(pair => pair.Value.IsCompleted)) cancellations.TryRemove(pair.Key, out _);
        var cleanup = coordinator.CancelAsync(identity.RunId, identity.Scope, CancellationToken.None).AsTask();
        cancellations[identity.RunId] = ObserveCleanup(cleanup, identity.RunId);
    }
    private async Task ObserveCleanup(Task<bool> cleanup, string runId)
    {
        try { await cleanup; }
        catch (Exception) { /* Coordinator retains the safe local failure; no automatic replay. */ }
        finally { registrations.TryRemove(runId, out _); cancellations.TryRemove(runId, out _); }
    }
    private Registration Get(AgentRunIdentity identity) =>
        registrations.TryGetValue(identity.RunId, out var registration) && registration.Identity == identity
            ? registration : throw new AgentStateException("RUN_SCOPE_CHANGED");
    private async ValueTask<ConversationSendResult> Finish(AgentRunResult result, CancellationToken token)
    {
        var identity = result.Snapshot.Identity;
        if (!result.Snapshot.IsTerminal)
            return new(result.Snapshot.Status == AgentRunStatus.WaitingApproval ? ConversationSendStatus.WaitingApproval
                : ConversationSendStatus.WaitingUserInput, identity.Scope.ConversationId) { RunId = identity.RunId };
        try
        {
            if (result.StatePersisted && result.Snapshot.Status == AgentRunStatus.Completed)
            {
                fence.EnsureCurrent(identity, token);
                return (await Get(identity).Deliver(result, token)) with { RunId = identity.RunId };
            }
            return new(result.Snapshot.Status == AgentRunStatus.Cancelled ? ConversationSendStatus.Cancelled
                : ConversationSendStatus.Failed, identity.Scope.ConversationId,
                SafeError: result.Snapshot.Status == AgentRunStatus.ExecutionUnknown
                    ? "操作结果尚未确认，请先核对实际状态。" : "本次任务未完成，请重试。") { RunId = identity.RunId };
        }
        finally { registrations.TryRemove(identity.RunId, out _); }
    }
    private sealed class Fence(ConcurrentDictionary<string, Registration> registrations) : IAgentRunFence
    {
        public void EnsureCurrent(AgentRunIdentity identity, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!registrations.TryGetValue(identity.RunId, out var registration) || registration.Identity != identity)
                throw new AgentStateException("RUN_SCOPE_CHANGED");
            registration.CheckCurrent();
            token.ThrowIfCancellationRequested();
        }
    }
    private sealed class ScopedPipeline(ToolRegistry tools, IAgentRunFence fence, IToolPolicy? policy,
        ApprovalBroker? approvals) : IToolExecutionPipeline
    {
        public ValueTask<ToolExecutionOutcome> AdvanceAsync(ToolExecutionRequest request, IToolExecutionIntent intent,
            CancellationToken token) => new ToolExecutionPipeline(tools,
                new ToolExecutor(tools, request.Identity.Scope, fence), policy, approvals).AdvanceAsync(request, intent, token);
    }
}
