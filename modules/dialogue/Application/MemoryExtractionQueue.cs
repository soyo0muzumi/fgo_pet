using System.Threading.Channels;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Memory;
using FgoPet.Core.Settings;
using Microsoft.Extensions.Logging;

namespace FgoPet.App.Dialogue;

public sealed class MemoryExtractionQueue : IAsyncDisposable, IDisposable
{
    private readonly Channel<Job> _jobs = Channel.CreateBounded<Job>(new BoundedChannelOptions(8)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
    private readonly IMemoryCandidateExtractor _extractor;
    private readonly IMemoryCandidateSink _sink;
    private readonly IMemoryWriteLifetime _writes;
    private readonly DialogueContextLifetime _lifetime;
    private readonly Func<ModelConnectionSettings, bool> _isCurrent;
    private readonly Func<MemorySource, bool> _isSourceCurrent;
    private readonly ILogger<MemoryExtractionQueue>? _logger;
    private readonly Task _worker;
    private readonly TimeSpan _drainTimeout;
    private int _closed;
    private int _hasWork;
    public MemoryExtractionQueue(IMemoryCandidateExtractor extractor, IMemoryCandidateSink sink, IMemoryWriteLifetime writes,
        DialogueContextLifetime lifetime, Func<ModelConnectionSettings, bool> isCurrent,
        ILogger<MemoryExtractionQueue>? logger = null, TimeSpan? drainTimeout = null, Func<MemorySource, bool>? isSourceCurrent = null)
    {
        _extractor = extractor; _sink = sink; _writes = writes; _lifetime = lifetime; _isCurrent = isCurrent; _logger = logger;
        _isSourceCurrent = isSourceCurrent ?? (_ => true);
        _drainTimeout = drainTimeout ?? TimeSpan.FromSeconds(3);
        _worker = RunAsync();
    }
    public bool TryEnqueue(MemoryExtractionWork work)
    {
        Interlocked.Exchange(ref _hasWork, 1);
        var lease = _lifetime.Acquire(work.CancellationToken);
        if (Volatile.Read(ref _closed) == 0 && _isCurrent(work.Connection) && _jobs.Writer.TryWrite(new(work, lease))) return true;
        lease.Dispose();
        _sink.Abandon(work.Ticket);
        _logger?.LogDebug("Memory extraction not queued");
        return false;
    }
    private async Task RunAsync()
    {
        await foreach (var job in _jobs.Reader.ReadAllAsync().ConfigureAwait(false))
            await ProcessAsync(job.Work, job.Lease).ConfigureAwait(false);
    }

    /// <summary>
    /// Processes one admitted job and owns its lease through receipt cleanup. Kept separate
    /// from channel scheduling so the pre-extraction and commit fences have one testable path.
    /// </summary>
    internal async Task ProcessAsync(MemoryExtractionWork work, DialogueContextLifetime.Lease lease)
    {
        using (lease)
        {
            try
            {
                lease.CheckCurrent();
                if (!_isCurrent(work.Connection) || !_isSourceCurrent(work.Ticket.Source)) return;
                // WaitAsync detaches a provider that ignores cancellation; its result has no write continuation.
                var proposals = await _extractor.ExtractAsync(work, lease.Token).WaitAsync(lease.Token).ConfigureAwait(false);
                lease.Commit(() =>
                {
                    if (Volatile.Read(ref _closed) == 0 && _isCurrent(work.Connection) && _isSourceCurrent(work.Ticket.Source))
                        _sink.Stage(work.Ticket, proposals);
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception) { _logger?.LogWarning("Memory extraction failed; no approved memory written"); }
            finally
            {
                try { _sink.Abandon(work.Ticket); }
                catch (Exception) { _logger?.LogWarning("Memory extraction receipt cleanup failed"); }
            }
        }
    }
    public async Task CancelAndDrainAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
        {
            // A constructed-but-never-started UI has no database session or tickets to invalidate.
            if (Volatile.Read(ref _hasWork) != 0) _writes.InvalidateWrites();
            _lifetime.Invalidate();
            _jobs.Writer.TryComplete();
        }
        try { await _worker.WaitAsync(_drainTimeout, cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException) { _logger?.LogWarning("Memory extraction drain timed out; writes remain fenced"); }
    }
    public ValueTask DisposeAsync() => new(CancelAndDrainAsync(CancellationToken.None));
    public void Dispose() => CancelAndDrainAsync(CancellationToken.None).GetAwaiter().GetResult();
    private sealed record Job(MemoryExtractionWork Work, DialogueContextLifetime.Lease Lease);
}
