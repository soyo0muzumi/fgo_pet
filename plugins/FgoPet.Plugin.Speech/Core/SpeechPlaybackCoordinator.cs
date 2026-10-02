using FgoPet.Core.Speech;
using FgoPet.Speech.Settings;

namespace FgoPet.App.Speech;

/// <summary>Coordinates completed-message speech, playback generation and cleanup.</summary>
public sealed class SpeechPlaybackCoordinator : IConfiguredSpeechPlayback, IDisposable
{
    private readonly SpeechSynthesisCoordinator _synthesis;
    private readonly ISpeechAudioPlayer _player;
    private readonly ISpeechSettingsStore _settings;
    private readonly SpeechOperationLifetime _lifetime = new();
    private int _playerDisposed;
    internal void BindLifecycle(CancellationToken stoppingToken) => _lifetime.Bind(stoppingToken);

    public SpeechPlaybackCoordinator(
        SpeechSynthesisCoordinator synthesis,
        ISpeechAudioPlayer player,
        ISpeechSettingsStore settings)
    {
        _synthesis = synthesis ?? throw new ArgumentNullException(nameof(synthesis));
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public SpeechPlaybackState State { get; private set; } = SpeechPlaybackState.Idle;
    public event EventHandler? StateChanged;

    public async Task<SpeechPlaybackResult> PlayConfiguredAsync(
        string? text,
        bool autoRead = false,
        CancellationToken cancellationToken = default)
    {
        _lifetime.EnsureOpen();
        try
        {
            var configuration = _settings.Load().Connection.Normalize();
            if (autoRead)
            {
                if (!configuration.AutoReadEnabled)
                {
                    return new(false, SpeechPlaybackState.Idle, "自动朗读未启用。");
                }

                if (configuration.DoNotDisturb)
                {
                    return new(false, SpeechPlaybackState.Idle, "免打扰已开启。");
                }

                if (!SpeechTextFilter.TryGetAutoReadText(text, configuration.AutoReadLimit, out var autoReadText))
                {
                    var filtered = SpeechTextFilter.Filter(text);
                    return new(
                        false,
                        SpeechPlaybackState.Idle,
                        filtered.Length > configuration.AutoReadLimit
                            ? "回复较长，请手动朗读。"
                            : "没有可朗读的正文。");
                }

                text = autoReadText;
            }

            var request = configuration.CreateRequest();
            return await PlayAsync(request, text, configuration.Playback, cancellationToken).ConfigureAwait(false);
        }
        catch (SpeechSynthesisException error)
        {
            SetState(SpeechPlaybackState.Failed);
            return new(false, SpeechPlaybackState.Failed, error.Message);
        }
        catch (ArgumentException)
        {
            SetState(SpeechPlaybackState.Failed);
            return new(false, SpeechPlaybackState.Failed, "朗读设置无效。");
        }
    }

    public async Task<SpeechPlaybackResult> PlayAsync(
        SpeechSynthesisRequest request,
        string? text,
        SpeechPlaybackOptions options,
        CancellationToken cancellationToken = default)
    {
        _lifetime.EnsureOpen();
        ArgumentNullException.ThrowIfNull(request);
        var segments = SpeechTextFilter.SplitForSynthesis(text);
        if (segments.Count == 0)
        {
            SetState(SpeechPlaybackState.Idle);
            return new(false, SpeechPlaybackState.Idle, "没有可朗读的正文。");
        }

        using var operation = _lifetime.Begin(cancellationToken);
        _synthesis.Stop();
        _player.Stop();
        SetStateIfCurrent(operation, SpeechPlaybackState.Preparing);
        try
        {
            foreach (var segment in segments)
            {
                EnsureCurrent(operation);
                var audio = await _synthesis.SynthesizeAsync(request, segment, operation.Token)
                    .ConfigureAwait(false);
                if (audio is null)
                {
                    throw new SpeechSynthesisException(SpeechFailureCategory.InvalidResponse, "语音服务未返回音频。");
                }
                EnsureCurrent(operation);
                SetStateIfCurrent(operation, SpeechPlaybackState.Playing);
                await _player.PlayAsync(audio, options, operation.Token).ConfigureAwait(false);
            }

            SetStateIfCurrent(operation, SpeechPlaybackState.Ended);
            return new(true, SpeechPlaybackState.Ended);
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
            SetStateIfCurrent(operation, SpeechPlaybackState.Stopped);
            return new(false, SpeechPlaybackState.Stopped, "朗读已停止。");
        }
        catch (SpeechSynthesisException error)
        {
            SetStateIfCurrent(operation, SpeechPlaybackState.Failed);
            return new(false, SpeechPlaybackState.Failed, error.Message);
        }
        catch (Exception)
        {
            SetStateIfCurrent(operation, SpeechPlaybackState.Failed);
            return new(false, SpeechPlaybackState.Failed, "朗读播放失败，可重试。");
        }
    }

    public void Stop()
    {
        _lifetime.Stop();
        _synthesis.Stop();
        if (Volatile.Read(ref _playerDisposed) == 0) _player.Stop();
        SetState(SpeechPlaybackState.Stopped);
    }

    public async Task CancelAndDrainAsync()
    {
        var drain = _lifetime.CloseAndDrainAsync();
        _synthesis.Stop();
        if (Volatile.Read(ref _playerDisposed) == 0) _player.Stop();
        await drain.ConfigureAwait(false);
        DisposePlayer();
    }

    public void Dispose()
    {
        var drain = CancelAndDrainAsync();
        if (!drain.IsCompletedSuccessfully) _ = ObserveDrainAsync(drain);
    }
    private static async Task ObserveDrainAsync(Task drain)
    {
        try { await drain.ConfigureAwait(false); }
        catch { } // The awaited plugin stop is the authoritative cleanup boundary.
    }
    private void DisposePlayer()
    {
        if (Interlocked.Exchange(ref _playerDisposed, 1) == 0) _player.Dispose();
    }
    private void EnsureCurrent(SpeechOperationLifetime.Operation operation)
    {
        operation.Token.ThrowIfCancellationRequested();
        if (!_lifetime.IsCurrent(operation)) throw new OperationCanceledException(operation.Token);
    }
    private void SetStateIfCurrent(SpeechOperationLifetime.Operation operation, SpeechPlaybackState state)
    {
        if (_lifetime.IsCurrent(operation)) SetState(state);
    }

    private void SetState(SpeechPlaybackState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

}
