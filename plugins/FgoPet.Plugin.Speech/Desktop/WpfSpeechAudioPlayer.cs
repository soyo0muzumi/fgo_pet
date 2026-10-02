using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using FgoPet.Core.Speech;

namespace FgoPet.App.Speech;

/// <summary>Plays one WAV at a time from a per-session temporary file.</summary>
public sealed class WpfSpeechAudioPlayer : ISpeechAudioPlayer
{
    private readonly object _gate = new();
    private CancellationTokenSource? _active;
    private MediaPlayer? _mediaPlayer;
    private Dispatcher? _dispatcher;
    private bool _disposed;

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
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        MediaPlayer? player = null;
        EventHandler? opened = null;
        EventHandler? ended = null;
        EventHandler<ExceptionEventArgs>? failed = null;
        try
        {
            await File.WriteAllBytesAsync(file, audio.WavBytes, cancellationToken).ConfigureAwait(false);

            await dispatcher.InvokeAsync(() =>
            {
                player = new MediaPlayer
                {
                    Volume = new SpeechPlaybackOptions(options.Rate, options.Volume).SafeVolume,
                    SpeedRatio = new SpeechPlaybackOptions(options.Rate, options.Volume).SafeRate,
                };
                opened = (_, _) => player.Play();
                ended = (_, _) => completion.TrySetResult(true);
                failed = (_, _) => completion.TrySetException(new InvalidOperationException("speech_playback_failed"));
                player.MediaOpened += opened;
                player.MediaEnded += ended;
                player.MediaFailed += failed;
                player.Open(new System.Uri(file, System.UriKind.Absolute));
            });

            lock (_gate)
            {
                _active = linked;
                _mediaPlayer = player;
                _dispatcher = dispatcher;
            }

            using var registration = linked.Token.Register(() =>
            {
                completion.TrySetCanceled(linked.Token);
                try
                {
                    if (dispatcher.CheckAccess()) player?.Stop();
                    else _ = dispatcher.BeginInvoke(new Action(() => player?.Stop()));
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

        active?.Cancel();
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