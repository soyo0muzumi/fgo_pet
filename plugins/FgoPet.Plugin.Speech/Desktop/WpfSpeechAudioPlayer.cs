using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using FgoPet.Core.Speech;

namespace FgoPet.App.Speech;

/// <summary>Plays one WAV at a time from a per-session temporary file.</summary>
public sealed class WpfSpeechAudioPlayer : ISpeechAudioPlayer
{
    private readonly Func<Action<double?>?>? _createPlaybackObserver;
    private readonly object _gate = new();
    private CancellationTokenSource? _active;
    private MediaPlayer? _mediaPlayer;
    private Dispatcher? _dispatcher;
    private bool _disposed;

    // Optional host binding; null means no presentation consumer. Each session gets its own sink.
    public WpfSpeechAudioPlayer(Func<Action<double?>?>? createPlaybackObserver = null)
        => _createPlaybackObserver = createPlaybackObserver;

    public async Task PlayAsync(
        SpeechSynthesisResult audio,
        SpeechPlaybackOptions options,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(audio);
        Stop();

        var sessionRoot = Path.Combine(Path.GetTempPath(), "fgo-pet", "speech-session");
        Directory.CreateDirectory(sessionRoot);
        var file = Path.Combine(sessionRoot, Guid.NewGuid().ToString("N") + ".wav");
        var dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var observer = _createPlaybackObserver?.Invoke();
        lock (_gate) _active = linked;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        MediaPlayer? player = null;
        DispatcherTimer? levelTimer = null;
        bool IsCurrent() { lock (_gate) return ReferenceEquals(_active, linked); }
        void Publish(double? level)
        {
            if (!IsCurrent()) return;
            try { observer?.Invoke(level); }
            catch (InvalidOperationException) { } // A closed UI dispatcher must not interrupt audio playback.
        }
        EventHandler? opened = null;
        EventHandler? ended = null;
        EventHandler<ExceptionEventArgs>? failed = null;
        try
        {
            var envelope = observer is null ? null : WavSpeechEnvelope.Create(audio.WavBytes);
            await File.WriteAllBytesAsync(file, audio.WavBytes, cancellationToken).ConfigureAwait(false);

            await dispatcher.InvokeAsync(() =>
            {
                linked.Token.ThrowIfCancellationRequested();
                if (!IsCurrent()) throw new OperationCanceledException(linked.Token);
                player = new MediaPlayer
                {
                    Volume = new SpeechPlaybackOptions(options.Rate, options.Volume).SafeVolume,
                    SpeedRatio = new SpeechPlaybackOptions(options.Rate, options.Volume).SafeRate,
                };
                if (envelope is { IsSupported: true })
                {
                    levelTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
                        { Interval = TimeSpan.FromMilliseconds(33) };
                    levelTimer.Tick += (_, _) =>
                    {
                        if (!IsCurrent() || linked.IsCancellationRequested) { levelTimer.Stop(); return; }
                        Publish(envelope.Sample(player.Position) * options.SafeVolume);
                    };
                }
                opened = (_, _) =>
                {
                    if (!IsCurrent() || linked.IsCancellationRequested) return;
                    player.Play();
                    if (levelTimer is not null) { Publish(0); levelTimer.Start(); }
                };
                ended = (_, _) => { levelTimer?.Stop(); Publish(null); completion.TrySetResult(true); };
                failed = (_, _) =>
                {
                    levelTimer?.Stop(); Publish(null);
                    completion.TrySetException(new InvalidOperationException("speech_playback_failed"));
                };
                player.MediaOpened += opened;
                player.MediaEnded += ended;
                player.MediaFailed += failed;
                player.Open(new System.Uri(file, System.UriKind.Absolute));
            });

            lock (_gate)
            {
                if (ReferenceEquals(_active, linked)) { _mediaPlayer = player; _dispatcher = dispatcher; }
            }

            using var registration = linked.Token.Register(() =>
            {
                completion.TrySetCanceled(linked.Token);
                try
                {
                    void CancelPlayback() { levelTimer?.Stop(); player?.Stop(); Publish(null); }
                    if (dispatcher.CheckAccess()) CancelPlayback();
                    else _ = dispatcher.BeginInvoke(new Action(CancelPlayback));
                }
                catch (InvalidOperationException) { }
            });
            await completion.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await dispatcher.InvokeAsync(() =>
                {
                    levelTimer?.Stop(); Publish(null);
                    if (player is null) return;
                    if (opened is not null) player.MediaOpened -= opened;
                    if (ended is not null) player.MediaEnded -= ended;
                    if (failed is not null) player.MediaFailed -= failed;
                    player.Stop();
                    player.Close();
                });
            }
            catch (InvalidOperationException) { }

            lock (_gate)
            {
                if (ReferenceEquals(_active, linked))
                {
                    _active = null;
                    _mediaPlayer = null;
                    _dispatcher = null;
                }
            }

            TryDelete(file);
        }
    }

    public void Stop()
    {
        CancellationTokenSource? active;
        MediaPlayer? player;
        Dispatcher? dispatcher;
        lock (_gate)
        {
            active = _active;
            player = _mediaPlayer;
            dispatcher = _dispatcher;
        }

        try { active?.Cancel(); }
        catch (ObjectDisposedException) { } // Captured session may finish concurrently.
        if (player is null || dispatcher is null) return;
        try
        {
            if (dispatcher.CheckAccess()) player.Stop();
            else _ = dispatcher.BeginInvoke(new Action(() => player.Stop()));
        }
        catch (InvalidOperationException) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    private static void TryDelete(string file)
    {
        try
        {
            if (File.Exists(file)) File.Delete(file);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
