using FgoPet.Core.Speech;

namespace FgoPet.App.Speech;

public enum SpeechSessionState { Idle, Synthesizing, Ready, Cancelled, Failed }

/// <summary>Owns synthesis state and cancellation. No automatic provider fallback.</summary>
public sealed class SpeechSynthesisCoordinator(ISpeechSynthesizer synthesizer) : IDisposable
{
    private readonly ISpeechSynthesizer _synthesizer = synthesizer ?? throw new ArgumentNullException(nameof(synthesizer));
    private readonly SpeechOperationLifetime _lifetime = new();
    public SpeechSessionState State { get; private set; } = SpeechSessionState.Idle;
    public event EventHandler? StateChanged;
    internal void BindLifecycle(CancellationToken stoppingToken) => _lifetime.Bind(stoppingToken);

    public async Task<SpeechSynthesisResult?> SynthesizeAsync(SpeechSynthesisRequest request, string text,
        CancellationToken cancellationToken = default)
    {
        _lifetime.EnsureOpen();
        ArgumentNullException.ThrowIfNull(request);
        var filtered = SpeechTextFilter.Filter(text);
        if (filtered.Length == 0) { SetState(SpeechSessionState.Idle); return null; }
        using var operation = _lifetime.Begin(cancellationToken);
        SetStateIfCurrent(operation, SpeechSessionState.Synthesizing);
        try
        {
            var result = await _synthesizer.SynthesizeAsync(request.WithText(filtered), operation.Token)
                .WaitAsync(operation.Token).ConfigureAwait(false);
            operation.Token.ThrowIfCancellationRequested();
            if (!_lifetime.IsCurrent(operation)) throw new OperationCanceledException(operation.Token);
            SetStateIfCurrent(operation, SpeechSessionState.Ready);
            return result;
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
            SetStateIfCurrent(operation, SpeechSessionState.Cancelled);
            throw;
        }
        catch { SetStateIfCurrent(operation, SpeechSessionState.Failed); throw; }
    }
    public void Stop() { _lifetime.Stop(); SetState(SpeechSessionState.Cancelled); }
    public Task CancelAndDrainAsync() => _lifetime.CloseAndDrainAsync();
    public void Dispose() => _ = _lifetime.CloseAndDrainAsync();
    private void SetStateIfCurrent(SpeechOperationLifetime.Operation operation, SpeechSessionState state)
    {
        if (_lifetime.IsCurrent(operation)) SetState(state);
    }
    private void SetState(SpeechSessionState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
