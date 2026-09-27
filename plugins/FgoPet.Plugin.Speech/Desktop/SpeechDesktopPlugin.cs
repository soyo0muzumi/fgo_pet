using FgoPet.Extensibility;

namespace FgoPet.App.Speech;

public sealed class SpeechDesktopPlugin(SpeechPlaybackCoordinator playback, SpeechSynthesisCoordinator synthesis) : IFgoPetPlugin, IDisposable
{
    private int _started;
    private readonly object _gate = new();
    private Task? _stop;
    public PluginManifest Manifest { get; } = new("firstparty.speech", "1.0.0", 1, []);
    public PluginContributions Contributions => PluginContributions.Empty;
    public ValueTask StartAsync(CancellationToken stoppingToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stop is not null, this);
            if (Interlocked.Exchange(ref _started, 1) == 0)
            {
                synthesis.BindLifecycle(stoppingToken);
                playback.BindLifecycle(stoppingToken);
            }
        }
        return ValueTask.CompletedTask;
    }
    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        lock (_gate) return new(_stop ??= StopCoreAsync());
    }
    private async Task StopCoreAsync()
    {
        var playbackDrain = playback.CancelAndDrainAsync();
        var synthesisDrain = synthesis.CancelAndDrainAsync();
        await Task.WhenAll(playbackDrain, synthesisDrain).ConfigureAwait(false);
    }
    public ValueTask DisposeAsync() => StopAsync(CancellationToken.None);
    public void Dispose() { playback.Dispose(); synthesis.Dispose(); }
}
