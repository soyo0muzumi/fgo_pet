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
        await using var queue = new MemoryExtractionQueue(extractor, sink, sink, lifetime, _ => true, drainTimeout: TimeSpan.FromMilliseconds(100));
        using (var parent = lifetime.Acquire(default)) Assert.True(queue.TryEnqueue(Work("0")));
        await extractor.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        for (var i = 1; i <= 8; i++) Assert.True(queue.TryEnqueue(Work(i.ToString())));
        Assert.False(queue.TryEnqueue(Work("full")));
        await queue.CancelAndDrainAsync(default);
        extractor.Release.TrySetResult([new("late")]);
        Assert.False(queue.TryEnqueue(Work("closed")));
        Assert.Empty(sink.Staged);
        Assert.Contains("full", sink.Abandoned);
        Assert.Equal(1, sink.Invalidations);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Invalidation_or_configuration_change_prevents_staging(bool invalidate)
    {
        var lifetime = new DialogueContextLifetime();
        var sink = new Sink();
        var extractor = new Extractor();
        var current = true;
        await using var queue = new MemoryExtractionQueue(extractor, sink, sink, lifetime, _ => current);
        queue.TryEnqueue(Work("first"));
        await extractor.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        if (invalidate) await lifetime.InvalidateAsync(default);
        else current = false;
        extractor.Release.TrySetResult([new("late")]);
        await queue.CancelAndDrainAsync(default);
        Assert.Empty(sink.Staged);
    }
    private static MemoryExtractionWork Work(string id) => new(new(id, "g", 0,
        new(new("mash", null), "c", "m", "fingerprint", DateTimeOffset.UtcNow, MemoryEvidenceKind.UserStatement)), "我喜欢茶", "收到", Connection, default);
    private sealed class Extractor : IMemoryCandidateExtractor
    {
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<IReadOnlyList<MemoryProposal>> Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<MemoryProposal>> ExtractAsync(MemoryExtractionWork work, CancellationToken cancellationToken)
        { Started.TrySetResult(); return Release.Task; }
    }
    private sealed class Sink : IMemoryCandidateSink, IMemoryWriteLifetime
    {
        public List<string> Staged = [];
        public List<string> Abandoned = [];
        public int Invalidations;
        public MemoryWriteTicket Begin(MemorySource source) => throw new NotSupportedException();
        public MemoryStageResult Stage(MemoryWriteTicket ticket, IReadOnlyList<MemoryProposal> proposals) { Staged.Add(ticket.TicketId); return new(MemoryStageStatus.Staged, []); }
        public void Abandon(MemoryWriteTicket ticket) { lock (Abandoned) Abandoned.Add(ticket.TicketId); }
        public void StartSession() { }
        public void InvalidateWrites() => Invalidations++;
    }
}
