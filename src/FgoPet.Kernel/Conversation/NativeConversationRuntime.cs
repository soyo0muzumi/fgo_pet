using System.Collections.Concurrent;
using System.Collections.Immutable;
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
    private readonly ToolRegistry tools;
    private readonly NativeAgentExtensions extensions;
    private readonly Action<AgentRunSnapshot, string, bool?>? diagnostics;
    private readonly TimeProvider time;
    private readonly Action<string>? releaseAttachments;
    private int closed;
    private readonly ConcurrentDictionary<string, Task> cancellations = new(StringComparer.Ordinal);

    public NativeConversationRuntime(IAgentRunStore store, ToolRegistry tools, NativeAgentExtensions extensions,
        TimeProvider? time = null, IToolPolicy? policy = null, Func<bool>? admissionAvailable = null,
        Action<AgentRunSnapshot, string, bool?>? diagnostics = null, Action<string>? releaseAttachments = null)
    {
        this.admissionAvailable = admissionAvailable ?? (() => true);
        this.tools = tools;
        this.extensions = extensions;
        this.diagnostics = diagnostics;
        this.time = time ?? TimeProvider.System;
        this.releaseAttachments = releaseAttachments;
        fence = new(registrations);
        coordinator = new(store, (identity, token) => Get(identity).Model(token),
            new StepEnvironmentBuilder(tools.GetTools), new ScopedPipeline(tools, fence, policy, extensions.Approvals),
            fence, time, extensions);
    }
    // Admission is still rechecked per step; this view only reserves input space before optional context selection.
    internal ImmutableArray<ToolDescriptor> GetPreparationTools(ToolScope scope) =>
        NativeAgentEnvironment.AddHostTools(extensions, tools.GetTools(scope), finalOutput: true);

    internal Action<ContextBudgetDiagnostic>? ContextDiagnosticObserver => extensions.ContextDiagnostics;

    public async ValueTask<ConversationSendResult> StartAsync(AgentRunRequest request,
        Func<CancellationToken, ValueTask<IAgentModelStep>> model, Action checkCurrent,
        Func<AgentRunResult, CancellationToken, ValueTask<ConversationSendResult>> deliver, CancellationToken token)
    {
        if (Volatile.Read(ref closed) != 0 || !admissionAvailable()) throw new AgentStateException("RUN_HOST_CLOSED");
        var registration = new Registration(request.Identity, checkCurrent, model, deliver);
        if (!registrations.TryAdd(request.Identity.RunId, registration)) throw new AgentStateException("RUN_ALREADY_ACTIVE");
        try { return await Finish(await coordinator.StartAsync(request, token), token); }
        catch (Exception error)
        {
            if (error is not OperationCanceledException)
                Report(new AgentRunSnapshot { Identity = request.Identity, Status = AgentRunStatus.Failed,
                    StartedAt = this.time.GetUtcNow(), ErrorCode = error switch {
                        AgentStateException state => state.Code,
                        AgentProtocolException protocol => protocol.Code,
                        _ => "RUN_START_FAILED" } }, "runtime_exception", null);
            registrations.TryRemove(request.Identity.RunId, out _);
            releaseAttachments?.Invoke(request.Identity.RunId);
            throw;
        }
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
        if (cancelled) { registrations.TryRemove(runId, out _); releaseAttachments?.Invoke(runId); }
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
        foreach (var runId in registrations.Keys) releaseAttachments?.Invoke(runId);
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
        finally { registrations.TryRemove(runId, out _); cancellations.TryRemove(runId, out _); releaseAttachments?.Invoke(runId); }
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
            if (result.Snapshot.Status != AgentRunStatus.Cancelled)
                Report(result.Snapshot, "terminal_failure", result.StatePersisted);
            return new(result.Snapshot.Status == AgentRunStatus.Cancelled ? ConversationSendStatus.Cancelled
                : ConversationSendStatus.Failed, identity.Scope.ConversationId,
                SafeError: FailureMessage(result.Snapshot)) { RunId = identity.RunId };
        }
        finally { registrations.TryRemove(identity.RunId, out _); releaseAttachments?.Invoke(identity.RunId); }
    }
    private static string FailureMessage(AgentRunSnapshot snapshot)
    {
        var code = snapshot.ErrorCode is { Length: > 0 and <= 96 } value
            && value.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_') ? value : "RUN_FAILED";
        var message = snapshot.Status == AgentRunStatus.ExecutionUnknown
            ? "操作结果尚未确认，请先核对实际状态，不要直接重复该操作。"
            : code switch {
                "MODEL_OUTPUT_LIMIT" => "本轮回复达到输出上限，未形成完整答复。请要求更简短的回答，或分段继续。",
                "MODEL_NO_VISIBLE_OUTPUT" => "模型本轮未返回可见答复，未形成最终答复。请核对模型服务及本轮任务状态，避免重复已执行的操作。",
                "RUN_BUDGET_EXCEEDED" => "本轮已达到运行预算上限，尚未完成。请缩小本次请求的范围或分段继续。",
                "RUN_STORE_FAILED" => "本地任务状态未能可靠保存。请先核对实际状态，不要直接重复可能已执行的操作。",
                _ => "本次任务未完成。请保留错误代码并核对模型连接与服务状态，再决定是否重试。" };
        return $"{message}（错误代码：{code}）";
    }
    private void Report(AgentRunSnapshot snapshot, string stage, bool? persisted)
    {
        try { diagnostics?.Invoke(snapshot, stage, persisted); }
        catch (Exception) { /* Secondary diagnostics must not change a request's outcome or cause a replay. */ }
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
