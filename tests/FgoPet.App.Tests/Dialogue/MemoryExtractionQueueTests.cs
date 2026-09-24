using System.Collections.Concurrent;
using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Memory;
using FgoPet.Core.Settings;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

public sealed class MemoryExtractionQueueTests
{
    private static readonly ModelConnectionSettings Connection = new("test", "https://fixture.test", "m");

    [Fact]
    public async Task Disposing_a_never_used_queue_does_not_open_or_modify_a_database()
    {
        var sink = new Sink();
        await using (var queue = new MemoryExtractionQueue(new Extractor(), sink, sink, new(), _ => true)) { }
        Assert.Equal(0, sink.Invalidations);
    }

    [Fact]
    public async Task Queue_owns_its_lease_is_bounded_and_rejects_late_results_after_shutdown()
    {
        var lifetime = new DialogueContextLifetime();
        var sink = new Sink();
        var extractor = new Extractor();
        await using var queue = new MemoryExtractionQueue(extractor, sink, sink, lifetime, _ => true,
            drainTimeout: TimeSpan.FromMilliseconds(100));
        using (var parent = lifetime.Acquire(default)) Assert.True(queue.TryEnqueue(Work("0")));
        try
        {
            await AwaitBoundary(extractor.Started.Task, "queue-start");
            for (var i = 1; i <= 8; i++) Assert.True(queue.TryEnqueue(Work(i.ToString())));
            Assert.False(queue.TryEnqueue(Work("full")));
            await queue.CancelAndDrainAsync(default);
            extractor.Release.TrySetResult([new("late")]);
            Assert.False(queue.TryEnqueue(Work("closed")));
            // Drain may return at its existing deadline. Observe every accepted receipt
            // before asserting that a cancellation-ignoring extractor could not write.
            await AwaitBoundary(Task.WhenAll(Enumerable.Range(0, 9).Select(i => sink.Receipt(i.ToString()))), "queue-drain");
            Assert.Empty(sink.StageAttempts);
            Assert.Contains("full", sink.Abandoned);
            Assert.Contains("closed", sink.Abandoned);
            Assert.Equal(1, sink.Invalidations);
            Assert.Equal(1, extractor.Calls);
            Assert.True(extractor.Token.IsCancellationRequested);
        }
        finally { extractor.Release.TrySetResult([]); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Invalidation_or_configuration_change_prevents_staging(bool invalidate)
    {
        var lifetime = new DialogueContextLifetime();
        var sink = new Sink();
        var extractor = new Extractor();
        var current = 1;
        await using var queue = new MemoryExtractionQueue(extractor, sink, sink, lifetime, _ => Volatile.Read(ref current) == 1);
        var work = Work("first");
        var lease = lifetime.Acquire(work.CancellationToken);
        // Exercise the exact method called by the queue, but start the job synchronously.
        // This test asserts a commit fence, not how fast the channel gets a pool thread.
        var processing = queue.ProcessAsync(work, lease);
        try
        {
            Assert.True(extractor.Started.Task.IsCompletedSuccessfully);
            Assert.False(processing.IsCompleted);
            if (invalidate) lifetime.Invalidate();
            else Volatile.Write(ref current, 0);
            extractor.Release.TrySetResult([new("late")]);
            await AwaitBoundary(processing, "job-completion");

            // Crucially, no CancelAndDrainAsync has closed the queue before this assertion.
            Assert.Empty(sink.StageAttempts);
            Assert.Equal("first", Assert.Single(sink.Abandoned));
            Assert.Equal(0, sink.Invalidations);
            Assert.Throws<OperationCanceledException>(() => lease.CheckCurrent());
            Assert.Equal(invalidate, extractor.Token.IsCancellationRequested);

            // A valid job on the same still-open queue must remain able to stage.
            Volatile.Write(ref current, 1);
            var next = Work("next");
            await AwaitBoundary(queue.ProcessAsync(next, lifetime.Acquire(next.CancellationToken)), "next-job");
            Assert.Equal("next", Assert.Single(sink.Staged));
        }
        finally
        {
            extractor.Release.TrySetResult([]);
            await AwaitBoundary(processing, "job-cleanup");
        }
    }

    [Fact]
    public async Task Source_change_during_extraction_blocks_staging_without_closing_the_queue()
    {
        var lifetime = new DialogueContextLifetime();
        var sink = new Sink();
        var extractor = new Extractor();
        var current = 1;
        await using var queue = new MemoryExtractionQueue(extractor, sink, sink, lifetime, _ => true,
            isSourceCurrent: _ => Volatile.Read(ref current) == 1);
        var work = Work("source");
        var processing = queue.ProcessAsync(work, lifetime.Acquire(default));
        try
        {
            Assert.True(extractor.Started.Task.IsCompletedSuccessfully);
            Volatile.Write(ref current, 0);
            extractor.Release.TrySetResult([new("late")]);
            await AwaitBoundary(processing, "source-recheck");
            Assert.Empty(sink.StageAttempts);
            Assert.Equal("source", Assert.Single(sink.Abandoned));
            Assert.Equal(0, sink.Invalidations);
            Assert.False(extractor.Token.IsCancellationRequested);
        }
        finally
        {
            extractor.Release.TrySetResult([]);
            await AwaitBoundary(processing, "source-cleanup");
        }
    }

    [Fact]
    public async Task Caller_cancellation_detaches_an_uncooperative_extractor_and_disposes_the_lease()
    {
        var lifetime = new DialogueContextLifetime();
        var sink = new Sink();
        var extractor = new Extractor();
        using var caller = new CancellationTokenSource();
        await using var queue = new MemoryExtractionQueue(extractor, sink, sink, lifetime, _ => true);
        var lease = lifetime.Acquire(caller.Token);
        var processing = queue.ProcessAsync(Work("caller") with { CancellationToken = caller.Token }, lease);
        try
        {
            Assert.True(extractor.Started.Task.IsCompletedSuccessfully);
            caller.Cancel();
            await AwaitBoundary(processing, "caller-cancellation");
            Assert.False(extractor.Release.Task.IsCompleted);
            Assert.True(extractor.Token.IsCancellationRequested);
            Assert.Empty(sink.StageAttempts);
            Assert.Equal("caller", Assert.Single(sink.Abandoned));
            Assert.Throws<OperationCanceledException>(() => lease.CheckCurrent());
        }
        finally
        {
            extractor.Release.TrySetResult([new("late")]);
            await AwaitBoundary(processing, "caller-cleanup");
        }
    }

    [Theory]
    [InlineData("configuration")]
    [InlineData("source")]
    [InlineData("lease")]
    public async Task Rejected_job_releases_its_lease_without_starting_extraction(string rejection)
    {
        var lifetime = new DialogueContextLifetime();
        var sink = new Sink();
        var extractor = new Extractor();
        await using var queue = new MemoryExtractionQueue(extractor, sink, sink, lifetime,
            _ => rejection != "configuration", isSourceCurrent: _ => rejection != "source");
        var lease = lifetime.Acquire(default);
        if (rejection == "lease") lifetime.Invalidate();
        await AwaitBoundary(queue.ProcessAsync(Work("rejected"), lease), "early-rejection");
        Assert.Equal(0, extractor.Calls);
        Assert.Empty(sink.StageAttempts);
        Assert.Equal("rejected", Assert.Single(sink.Abandoned));
        Assert.Throws<OperationCanceledException>(() => lease.CheckCurrent());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Job_failure_abandons_the_ticket_and_releases_the_lease(bool sinkFailure)
    {
        var lifetime = new DialogueContextLifetime();
        var sink = new Sink { FailNextStage = sinkFailure ? 1 : 0 };
        var extractor = new Extractor();
        if (sinkFailure) extractor.Release.SetResult([new("candidate")]);
        else extractor.Release.SetException(new InvalidOperationException("fixture-extractor-failure"));
        await using var queue = new MemoryExtractionQueue(extractor, sink, sink, lifetime, _ => true);
        var lease = lifetime.Acquire(default);
        await AwaitBoundary(queue.ProcessAsync(Work("failed"), lease), "failure-cleanup");
        Assert.Empty(sink.Staged);
        Assert.Equal(sinkFailure ? 1 : 0, sink.StageAttempts.Count);
        Assert.Equal("failed", Assert.Single(sink.Abandoned));
        Assert.Throws<OperationCanceledException>(() => lease.CheckCurrent());
    }

    [Fact]
    public async Task Real_queue_stages_current_work_and_recovers_from_a_failed_job_before_shutdown()
    {
        var lifetime = new DialogueContextLifetime();
        var sink = new Sink();
        var extractor = new Extractor { FailForTicket = "bad" };
        extractor.Release.SetResult([new("candidate")]);
        await using var queue = new MemoryExtractionQueue(extractor, sink, sink, lifetime, _ => true);
        Assert.True(queue.TryEnqueue(Work("bad")));
        Assert.True(queue.TryEnqueue(Work("good")));
        await AwaitBoundary(Task.WhenAll(sink.Receipt("bad"), sink.Receipt("good")), "queue-success-after-failure");
        Assert.Equal("good", Assert.Single(sink.Staged));
        Assert.Equal("good", Assert.Single(sink.StageAttempts));
        Assert.Equal<string>(["bad", "good"], sink.Abandoned);
        Assert.Equal(2, extractor.Calls);
        Assert.Equal(0, sink.Invalidations);
    }

    private static async Task AwaitBoundary(Task task, string phase)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (TimeoutException)
        {
            // Fixed fixture phase only: never print candidate text, tickets or user input.
            throw new TimeoutException($"memory-extraction-fixture: phase={phase}; taskState={task.Status}; deadline=2s");
        }
    }

    private static MemoryExtractionWork Work(string id) => new(new(id, "g", 0,
        new(new("mash", null), "c", "m", "fingerprint", DateTimeOffset.UtcNow, MemoryEvidenceKind.UserStatement)),
        "我喜欢茶", "收到", Connection, default);

    private sealed class Extractor : IMemoryCandidateExtractor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<IReadOnlyList<MemoryProposal>> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token { get; private set; }
        public string? FailForTicket { get; init; }
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public Task<IReadOnlyList<MemoryProposal>> ExtractAsync(MemoryExtractionWork work, CancellationToken cancellationToken)
        {
            Token = cancellationToken;
            Interlocked.Increment(ref _calls);
            Started.TrySetResult();
            return work.Ticket.TicketId == FailForTicket
                ? Task.FromException<IReadOnlyList<MemoryProposal>>(new InvalidOperationException("fixture-extractor-failure"))
                : Release.Task;
        }
    }

    private sealed class Sink : IMemoryCandidateSink, IMemoryWriteLifetime
    {
        public ConcurrentQueue<string> Staged { get; } = new();
        public ConcurrentQueue<string> StageAttempts { get; } = new();
        public ConcurrentQueue<string> Abandoned { get; } = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _receipts = new();
        private int _invalidations;
        public int Invalidations => Volatile.Read(ref _invalidations);
        public int FailNextStage;
        public Task Receipt(string id) => ReceiptSource(id).Task;
        private TaskCompletionSource ReceiptSource(string id) => _receipts.GetOrAdd(id,
            _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
        public MemoryWriteTicket Begin(MemorySource source) => throw new NotSupportedException();
        public MemoryStageResult Stage(MemoryWriteTicket ticket, IReadOnlyList<MemoryProposal> proposals)
        {
            StageAttempts.Enqueue(ticket.TicketId);
            if (Interlocked.Exchange(ref FailNextStage, 0) != 0) throw new InvalidOperationException("fixture-sink-failure");
            Staged.Enqueue(ticket.TicketId);
            return new(MemoryStageStatus.Staged, []);
        }
        public void Abandon(MemoryWriteTicket ticket)
        {
            Abandoned.Enqueue(ticket.TicketId);
            ReceiptSource(ticket.TicketId).TrySetResult();
        }
        public void StartSession() { }
        public void InvalidateWrites() => Interlocked.Increment(ref _invalidations);
    }
}
