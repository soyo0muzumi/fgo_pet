using FgoPet.Core.Speech;
using FgoPet.Speech.Settings;

namespace FgoPet.App.Speech;

public sealed record SpeechPlaybackResult(
    bool Completed,
    SpeechPlaybackState State,
    string? SafeError = null);

/// <summary>Coordinates completed-message speech, playback generation and cleanup.</summary>
public sealed class SpeechPlaybackCoordinator : IDisposable
{
    private readonly SpeechSynthesisCoordinator _synthesis;
    private readonly ISpeechAudioPlayer _player;
    private readonly ISpeechSettingsStore _settings;
    private readonly object _gate = new();
    private CancellationTokenSource? _active;
    private long _generation;
    private bool _disposed;

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
        ObjectDisposedException.ThrowIf(_disposed, this);
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
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        var segments = SpeechTextFilter.SplitForSynthesis(text);
        if (segments.Count == 0)
        {
            SetState(SpeechPlaybackState.Idle);
            return new(false, SpeechPlaybackState.Idle, "没有可朗读的正文。");
        }

        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationTokenSource? previous;
        long generation;
        lock (_gate)
        {
            previous = _active;
            _active = linked;
            generation = ++_generation;
        }

        previous?.Cancel();
        _synthesis.Stop();
        _player.Stop();
        SetStateIfCurrent(linked, generation, SpeechPlaybackState.Preparing);
        try
        {
            foreach (var segment in segments)
            {
                EnsureCurrent(linked, generation);
                var audio = await _synthesis.SynthesizeAsync(request, segment, linked.Token)
                    .ConfigureAwait(false);
                if (audio is null)
                {
                    throw new SpeechSynthesisException(SpeechFailureCategory.InvalidResponse, "语音服务未返回音频。");
                }
                EnsureCurrent(linked, generation);
                SetStateIfCurrent(linked, generation, SpeechPlaybackState.Playing);
                await _player.PlayAsync(audio, options, linked.Token).ConfigureAwait(false);
            }

            SetStateIfCurrent(linked, generation, SpeechPlaybackState.Ended);
            return new(true, SpeechPlaybackState.Ended);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            SetStateIfCurrent(linked, generation, SpeechPlaybackState.Stopped);
            return new(false, SpeechPlaybackState.Stopped, "朗读已停止。");
        }
        catch (SpeechSynthesisException error)
        {
            SetStateIfCurrent(linked, generation, SpeechPlaybackState.Failed);
            return new(false, SpeechPlaybackState.Failed, error.Message);
        }
        catch (Exception)
        {
            SetStateIfCurrent(linked, generation, SpeechPlaybackState.Failed);
            return new(false, SpeechPlaybackState.Failed, "朗读播放失败，可重试。");
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_active, linked)) _active = null;
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
            _active = null;
            _generation++;
        }

        active?.Cancel();
        _synthesis.Stop();
        _player.Stop();
        SetState(SpeechPlaybackState.Stopped);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _player.Dispose();
    }

    private void EnsureCurrent(CancellationTokenSource source, long generation)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_active, source) || _generation != generation)
            {
                throw new OperationCanceledException(source.Token);
            }
        }

        source.Token.ThrowIfCancellationRequested();
    }

    private void SetStateIfCurrent(CancellationTokenSource source, long generation, SpeechPlaybackState state)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_active, source) || _generation != generation) return;
            SetState(state);
        }
    }

    private void SetState(SpeechPlaybackState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

}
