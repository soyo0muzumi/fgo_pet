using FgoPet.App.Services;
using FgoPet.Core.Agents;
using FgoPet.Core.Settings;
using FgoPet.Core.Todo;
using Xunit;

namespace FgoPet.App.Tests.Services;

public sealed class AgentArchiveServiceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-02T08:00:00Z");

    [Fact]
    public void Builds_candidates_only_when_terminal_receipt_is_exact_and_orders_oldest_first()
    {
        var repository = new FakeAgentRepository();
        var eligible = Execution("execution-old", "todo-old", "task-old", Now.AddDays(-31));
        var newer = Execution("execution-new", "todo-new", "task-new", Now.AddDays(-29));
        var missingReceipt = Execution("execution-missing", "todo-missing", "task-missing", Now.AddDays(-40));
        repository.Executions.AddRange(new[] { newer, missingReceipt, eligible });
        repository.SetReceipt(eligible, 4);
        repository.SetReceipt(newer, 2);
        repository.LatestSequences[Key(missingReceipt)] = 3;

        var service = new AgentArchiveService(repository, new FakeAdministration(), new FixedTimeProvider(Now));

        var candidates = service.BuildCandidates();

        var candidate = Assert.Single(candidates);
        Assert.Equal("execution-old", candidate.ExecutionId);
        Assert.Equal(4, candidate.Identity.FinalSequence);
        Assert.Equal(AgentArchiveHashing.CandidateSha256(candidate.Identity, candidate.EndedAt), candidate.SummarySha256);
    }

    [Fact]
    public async Task Unknown_prepare_keeps_the_durable_preparing_batch_without_retrying_inside_the_call()
    {
        var repository = new FakeAgentRepository();
        var execution = Execution("execution-1", "todo-1", "task-1", Now.AddDays(-31));
        repository.Executions.Add(execution);
        repository.SetReceipt(execution, 1);
        var administration = new FakeAdministration
        {
            PrepareResult = (_, _) => Task.FromResult(new AgentArchivePrepareResult(
                "rejected", "ignored", "IGNORED", "relay_timeout")),
        };
        var service = new AgentArchiveService(repository, administration, new FixedTimeProvider(Now));

        var result = await service.RunAsync();

        Assert.Equal("unknown", result.Result);
        Assert.Equal(1, administration.PrepareCalls);
        Assert.Equal(AgentArchiveBatchState.Preparing, Assert.Single(repository.Batches.Values).State);
        Assert.Equal(result.BatchId, Assert.Single(repository.Batches.Values).BatchId);
    }

    [Fact]
    public async Task Unexpected_prepare_result_is_treated_as_unknown_without_advancing_the_batch()
    {
        var repository = new FakeAgentRepository();
        var execution = Execution("execution-1", "todo-1", "task-1", Now.AddDays(-31));
        repository.Executions.Add(execution);
        repository.SetReceipt(execution, 1);
        var administration = new FakeAdministration
        {
            PrepareResult = (_, _) => Task.FromResult(new AgentArchivePrepareResult(
                "unexpected", "ignored", "IGNORED")),
        };
        var service = new AgentArchiveService(repository, administration, new FixedTimeProvider(Now));

        var result = await service.RunAsync();

        Assert.Equal("unknown", result.Result);
        Assert.Equal("operation_unknown", result.SafeError);
        Assert.Equal(AgentArchiveBatchState.Preparing, Assert.Single(repository.Batches.Values).State);
        Assert.Equal(0, administration.CommitCalls);
    }

    [Fact]
    public async Task Unexpected_commit_result_is_treated_as_unknown_without_completing_the_batch()
    {
        var repository = new FakeAgentRepository();
        var execution = Execution("execution-1", "todo-1", "task-1", Now.AddDays(-31));
        repository.Executions.Add(execution);
        repository.SetReceipt(execution, 1);
        var administration = new FakeAdministration
        {
            CommitResult = (_, _, _) => Task.FromResult(new AgentArchiveCommitResult(
                "unexpected", "ignored", "IGNORED")),
        };
        var service = new AgentArchiveService(repository, administration, new FixedTimeProvider(Now));

        var result = await service.RunAsync();

        Assert.Equal("unknown", result.Result);
        Assert.Equal("operation_unknown", result.SafeError);
        Assert.Equal(AgentArchiveBatchState.CommitPending, Assert.Single(repository.Batches.Values).State);
    }

    [Fact]
    public async Task Accepted_prepare_and_commit_complete_the_same_batch()
    {
        var repository = new FakeAgentRepository();
        var execution = Execution("execution-1", "todo-1", "task-1", Now.AddDays(-31));
        repository.Executions.Add(execution);
        repository.SetReceipt(execution, 1);
        var administration = new FakeAdministration();
        var service = new AgentArchiveService(repository, administration, new FixedTimeProvider(Now));

        var result = await service.RunAsync();

        Assert.Equal("completed", result.Result);
        Assert.Equal(1, administration.PrepareCalls);
        Assert.Equal(1, administration.CommitCalls);
        Assert.Equal(AgentArchiveBatchState.Completed, Assert.Single(repository.Batches.Values).State);
    }

    [Fact]
    public async Task Active_or_unknown_work_blocks_archive_without_calling_relay()
    {
        var repository = new FakeAgentRepository();
        var eligible = Execution("execution-1", "todo-1", "task-1", Now.AddDays(-31));
        repository.Executions.Add(eligible);
        repository.SetReceipt(eligible, 1);
        repository.Executions.Add(new AgentExecution(
            "execution-active", "todo-active", "codex", "instance-1", "task-active", "dispatch-active", Now,
            AgentExecutionStatus.Active));
        var administration = new FakeAdministration();
        var service = new AgentArchiveService(repository, administration, new FixedTimeProvider(Now));

        var result = await service.RunAsync();

        Assert.Equal("blocked_active_work", result.Result);
        Assert.Equal("active_agent_work", result.SafeError);
        Assert.Equal(0, administration.PrepareCalls);
        Assert.Empty(repository.Batches);
    }

    private static AgentExecution Execution(string executionId, string todoId, string taskId, DateTimeOffset endedAt) =>
        new(executionId, todoId, "codex", "instance-1", taskId, "dispatch-" + taskId, endedAt,
            AgentExecutionStatus.Completed, endedAt, endedAt);

    private static string Key(AgentExecution execution) =>
        $"{execution.SourceType}/{execution.SourceInstance}/{execution.TaskId}";

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeAgentRepository : IAgentRepository
    {
        public List<AgentExecution> Executions { get; } = new();
        public Dictionary<string, long> LatestSequences { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Receipts { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, AgentArchiveBatch> Batches { get; } = new(StringComparer.Ordinal);

        public void SetReceipt(AgentExecution execution, long sequence)
        {
            LatestSequences[Key(execution)] = sequence;
            Receipts.Add($"{Key(execution)}/{sequence}");
        }

        public void SaveExecution(AgentExecution execution) => Executions.RemoveAll(item => item.Id == execution.Id);
        public AgentExecution? GetExecution(string id) => Executions.FirstOrDefault(item => item.Id == id);
        public AgentExecution? GetExecution(string sourceType, string sourceInstance, string taskId) =>
            Executions.FirstOrDefault(item => item.SourceType == sourceType && item.SourceInstance == sourceInstance && item.TaskId == taskId);
        public IReadOnlyList<AgentExecution> ListNonTerminalExecutions() => Executions.Where(item => !item.IsTerminal).ToArray();
        public IReadOnlyList<AgentExecution> ListTerminalExecutions(DateTimeOffset endedBefore, int limit) =>
            Executions.Where(item => item.IsTerminal && item.EndedAt is { } ended && ended < endedBefore)
                .OrderBy(item => item.EndedAt)
                .ThenBy(item => item.Id, StringComparer.Ordinal)
                .Take(limit)
                .ToArray();
        public bool HasEventReceipt(string sourceType, string sourceInstance, string taskId, long sequence) =>
            Receipts.Contains($"{sourceType}/{sourceInstance}/{taskId}/{sequence}");
        public long GetLatestEventSequence(string sourceType, string sourceInstance, string taskId) =>
            LatestSequences.TryGetValue($"{sourceType}/{sourceInstance}/{taskId}", out var sequence) ? sequence : 0;
        public void SaveArchiveBatch(AgentArchiveBatch batch) => Batches[batch.BatchId] = batch;
        public AgentArchiveBatch? GetArchiveBatch(string batchId) => Batches.TryGetValue(batchId, out var batch) ? batch : null;
        public IReadOnlyList<AgentArchiveBatch> ListIncompleteArchiveBatches() =>
            Batches.Values.Where(item => item.State is AgentArchiveBatchState.Preparing or AgentArchiveBatchState.Prepared or AgentArchiveBatchState.CommitPending).ToArray();
        public void CompleteArchiveBatch(string batchId, DateTimeOffset completedAt)
        {
            var batch = Batches[batchId];
            Batches[batchId] = new AgentArchiveBatch(batch.BatchId, batch.CreatedAt, AgentArchiveBatchState.Completed, batch.Candidates, batch.BatchSha256, batch.SafeError);
        }
        public AgentEventApplyResult ApplyEvent(AgentEvent agentEvent) => AgentEventApplyResult.Applied;
        public void SaveConnection(PersistedAgentConnection connection, IReadOnlyList<AgentProjectTarget> allowedTargets) { }
        public IReadOnlyList<PersistedAgentConnection> ListConnections() => Array.Empty<PersistedAgentConnection>();
    }

    private sealed class FakeAdministration : IAgentRelayAdministration
    {
        public Func<AgentArchiveBatch, CancellationToken, Task<AgentArchivePrepareResult>> PrepareResult { get; init; } =
            (batch, _) => Task.FromResult(new AgentArchivePrepareResult("accepted", batch.BatchId, batch.BatchSha256));
        public Func<string, string, CancellationToken, Task<AgentArchiveCommitResult>> CommitResult { get; init; } =
            (batchId, hash, _) => Task.FromResult(new AgentArchiveCommitResult("accepted", batchId, hash));
        public int PrepareCalls { get; private set; }
        public int CommitCalls { get; private set; }
        public Task<AgentRelaySnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) => Task.FromResult(AgentRelaySnapshot.Disabled);
        public Task<AgentRelaySnapshot> TestConnectionAsync(CancellationToken cancellationToken = default) => Task.FromResult(AgentRelaySnapshot.Disabled);
        public Task DecideRegistrationAsync(string requestId, bool approve, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdatePermissionsAsync(string sourceType, string sourceInstance, IReadOnlyList<string> targetIds, bool enabled, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RevokeSourceAsync(string sourceType, string sourceInstance, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<AgentArchivePrepareResult> PrepareArchiveAsync(AgentArchiveBatch batch, CancellationToken cancellationToken = default)
        {
            PrepareCalls++;
            return PrepareResult(batch, cancellationToken);
        }
        public Task<AgentArchiveCommitResult> CommitArchiveAsync(string batchId, string batchSha256, CancellationToken cancellationToken = default)
        {
            CommitCalls++;
            return CommitResult(batchId, batchSha256, cancellationToken);
        }
    }
}
