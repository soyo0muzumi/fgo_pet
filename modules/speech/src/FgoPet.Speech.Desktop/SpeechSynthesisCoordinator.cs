using FgoPet.Core.Speech;

namespace FgoPet.App.Speech;

public enum SpeechSessionState
{
    Idle,
    Synthesizing,
    Ready,
    Cancelled,
    Failed,
}

/// <summary>
/// Owns only speech synthesis cancellation/state. It does not share the chat or
/// Agent cancellation source, and it never chooses a fallback provider.
/// </summary>
public sealed class SpeechSynthesisCoordinator : IDisposable
{
    private readonly ISpeechSynthesizer _synthesizer;
    private readonly object _gate = new();
    private CancellationTokenSource? _active;
    private bool _disposed;

    public SpeechSynthesisCoordinator(ISpeechSynthesizer synthesizer) =>
        _synthesizer = synthesizer ?? throw new ArgumentNullException(nameof(synthesizer));

    public SpeechSessionState State { get; private set; } = SpeechSessionState.Idle;
    public event EventHandler? StateChanged;

    public async Task<SpeechSynthesisResult?> SynthesizeAsync(
        SpeechSynthesisRequest request,
        string text,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        var filtered = SpeechTextFilter.Filter(text);
        if (filtered.Length == 0)
        {
            SetState(SpeechSessionState.Idle);
            return null;
        }

        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_gate)
        {
            _active?.Cancel();
            _active = linked;
        }

        SetState(SpeechSessionState.Synthesizing);
        try
        {
            var result = await _synthesizer.SynthesizeAsync(request.WithText(filtered), linked.Token)
                .ConfigureAwait(false);
            SetStateIfCurrent(linked, SpeechSessionState.Ready);
            return result;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            SetStateIfCurrent(linked, SpeechSessionState.Cancelled);
            throw;
        }
        catch
        {
            SetStateIfCurrent(linked, SpeechSessionState.Failed);
            throw;
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_active, linked))
                {
                    _active = null;
                }
            }

            linked.Dispose();
        }
    }

    public void Stop()
    {
        CancellationTokenSource? active;
        lock (_gate)
        {
            active = _active;
        }

        active?.Cancel();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        lock (_gate)
        {
            _active = null;
        }
    }

    private void SetStateIfCurrent(CancellationTokenSource source, SpeechSessionState state)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_active, source))
            {
                return;
            }

            SetState(state);
        }
    }

    private void SetState(SpeechSessionState state)
    {
        if (State == state)
        {
            return;
        }

        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}