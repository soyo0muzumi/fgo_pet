using System.Collections.Immutable;
using FgoPet.Extensibility;
using FgoPet.Kernel.Agent;
using FgoPet.Agent.Tests.TestDoubles;
using Xunit;

namespace FgoPet.Agent.Tests.Runtime;

public sealed class RunStateTests
{
    [Fact]
    public async Task Store_timeout_is_fenced_instead_of_being_treated_as_caller_cancel()
    {
        var state = await CreateAsync(new TimeoutStore());
        var error = await Assert.ThrowsAsync<AgentStateException>(() => state.StartAsync(default).AsTask());
        Assert.Equal("RUN_STORE_FAILED", error.Code);
        Assert.True(state.IsFenced);
    }

    private sealed class TimeoutStore : IAgentRunStore
    {
        private readonly InMemoryAgentRunStore _inner = new();
        public ValueTask<AgentRunCheckpoint?> LoadAsync(string id, CancellationToken token) => _inner.LoadAsync(id, token);
        public ValueTask<bool> TryCreateAsync(AgentRunCheckpoint initial, ImmutableArray<AgentEvent> events, CancellationToken token)
            => _inner.TryCreateAsync(initial, events, token);
        public ValueTask<bool> TryCommitAsync(AgentRunCheckpoint next, long expected, ImmutableArray<AgentEvent> events,
            CancellationToken token) => throw new OperationCanceledException("synthetic storage timeout");
    }

    [Fact]
    public async Task Start_cannot_be_repeated_in_running_state()
    {
        var state = await CreateAsync(new InMemoryAgentRunStore());
        await state.StartAsync(default);
        await Assert.ThrowsAsync<AgentStateException>(() => state.StartAsync(default).AsTask());
    }

    [Fact]
    public async Task Initial_transcript_call_ids_cannot_be_reused()
    {
        var request = new AgentRunRequest(new("seeded", "user", new("conversation", "role", null), "route", 1), new(),
            [new(ModelMessageRole.User, "fixture", []), new(ModelMessageRole.Assistant, "", [new("a", "fixture.read", "{}")]),
                new(ModelMessageRole.Tool, "{}", [], "a")]);
        var state = await AgentRunState.CreateAsync(request, new InMemoryAgentRunStore(), TimeProvider.System, default);
        await state.StartAsync(default);
        await state.BeginStepAsync(default);
        await Assert.ThrowsAsync<AgentProtocolException>(() => state.AcceptResponseAsync(
            new(new(ModelMessageRole.Assistant, "", [new("a", "fixture.read", "{}")]), "tool_calls", true), default).AsTask());
    }

    [Fact]
    public async Task Store_compare_exchange_allows_one_writer_and_one_event_sequence()
    {
        var store = new InMemoryAgentRunStore();
        var state = await CreateAsync(store);
        var current = state.Checkpoint;
        var next = current with { Snapshot = current.Snapshot with { Revision = 1 }, JournalSequence = 2 };
        var events = ImmutableArray.Create(new AgentEvent(2, DateTimeOffset.UtcNow, AgentEventKind.RunStarted, "fixture", 0));
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<bool> Commit() { await start.Task; return await store.TryCommitAsync(next, 0, events, default); }
        var first = Task.Run(Commit);
        var second = Task.Run(Commit);
        start.SetResult();
        var commits = await Task.WhenAll(first, second);
        Assert.Single(commits.Where(value => value));
        Assert.Equal(1, (await store.LoadAsync("run", default))!.Snapshot.Revision);
        Assert.Equal(new long[] { 1, 2 }, store.Events("run").Select(e => e.Sequence));
    }

    [Fact]
    public async Task Last_model_reservation_is_valid_but_next_is_rejected()
    {
        var state = await CreateAsync(new InMemoryAgentRunStore(), new(1, 2, 1));
        await state.StartAsync(default);
        await state.ReserveAsync(default);
        Assert.Equal(1, state.Snapshot.ModelRequests);
        await Assert.ThrowsAsync<AgentBudgetExceededException>(() => state.ReserveAsync(default).AsTask());
        Assert.Equal(1, state.Snapshot.ModelRequests);
    }

    [Fact]
    public async Task Tool_reservation_is_once_per_call_and_cursor_advances_atomically()
    {
        var state = await CreateAsync(new InMemoryAgentRunStore());
        await state.StartAsync(default);
        await state.BeginStepAsync(default);
        await state.AcceptResponseAsync(new(new(ModelMessageRole.Assistant, "",
            [new("a", "fixture.read", "{}"), new("b", "fixture.read", "{}")]), "tool_calls", true), default);
        await state.ReserveToolAsync(default);
        await state.ReserveToolAsync(default);
        Assert.Equal(1, state.Snapshot.ToolCalls);
        await state.CompleteToolAsync(new(ToolExecutionOutcomeKind.Completed,
            new(true, System.Text.Json.JsonSerializer.SerializeToElement(new { value = 1 }))), default);
        Assert.Equal(1, state.Checkpoint.NextCallIndex);
        Assert.Equal(AgentCallStatus.Completed, state.Checkpoint.Calls[0].Status);
        await state.ReserveToolAsync(default);
        Assert.Equal(2, state.Snapshot.ToolCalls);
    }

    [Fact]
    public async Task Finished_state_cannot_reserve_or_return_to_running()
    {
        var state = await CreateAsync(new InMemoryAgentRunStore());
        await state.StartAsync(default);
        await state.FinishAsync(AgentRunStatus.Cancelled, "RUN_CANCELLED", default);
        await Assert.ThrowsAsync<AgentStateException>(() => state.ReserveAsync(default).AsTask());
        await Assert.ThrowsAsync<AgentStateException>(() => state.StartAsync(default).AsTask());
    }

    [Fact]
    public async Task Failed_store_commit_fences_future_advancement()
    {
        var store = new FaultInjectingAgentRunStore(new InMemoryAgentRunStore()) { FailOnCommit = 1 };
        var state = await CreateAsync(store);
        await Assert.ThrowsAsync<AgentStateException>(() => state.StartAsync(default).AsTask());
        Assert.True(state.IsFenced);
        await Assert.ThrowsAsync<AgentStateException>(() => state.StartAsync(default).AsTask());
        Assert.Equal(AgentRunStatus.Created, state.Snapshot.Status);
    }

    internal static ValueTask<AgentRunState> CreateAsync(IAgentRunStore store, RunBudget? budget = null) =>
        AgentRunState.CreateAsync(new(new("run", "user", new("conversation", "role", null), "route", 1),
            budget ?? new(), [new(ModelMessageRole.User, "fixture", [])]), store, TimeProvider.System, default);
}
