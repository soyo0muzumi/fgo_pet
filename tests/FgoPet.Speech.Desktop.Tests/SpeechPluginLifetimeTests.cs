using FgoPet.App.Speech;
using FgoPet.Core.Speech;
using FgoPet.Speech.Settings;
using Xunit;

namespace FgoPet.Speech.Desktop.Tests;

public sealed class SpeechPluginLifetimeTests
{
    [Fact]
    public async Task Stop_detaches_a_noncooperative_synthesizer_and_fences_new_requests()
    {
        var provider = new TestProvider { Hold = true };
        var synthesis = new SpeechSynthesisCoordinator(provider);
        var player = new Player();
        var playback = new SpeechPlaybackCoordinator(synthesis, player, new Settings());
        var plugin = new SpeechDesktopPlugin(playback, synthesis);
        await plugin.StartAsync(CancellationToken.None);
        var operation = playback.PlayConfiguredAsync("正文");
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await plugin.StopAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(SpeechPlaybackState.Stopped, (await operation).State);
        Assert.True(player.Disposed);
        Assert.Equal(0, player.Plays);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => playback.PlayConfiguredAsync("再次朗读"));
        provider.Result.SetResult(new("RIFF0000WAVE"u8.ToArray()));
        Assert.Equal(0, player.Plays);
    }

    [Fact]
    public async Task Stop_waits_for_audio_cleanup_before_disposal_and_process_cancellation_rejects_admission()
    {
        var synthesis = new SpeechSynthesisCoordinator(new TestProvider());
        var player = new Player { HoldCleanup = true };
        var playback = new SpeechPlaybackCoordinator(synthesis, player, new Settings());
        var plugin = new SpeechDesktopPlugin(playback, synthesis);
        using var process = new CancellationTokenSource();
        await plugin.StartAsync(process.Token);
        var operation = playback.PlayConfiguredAsync("正文");
        await player.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        process.Cancel();
        await player.Cleaning.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var drain = plugin.StopAsync(CancellationToken.None).AsTask();
        Assert.False(drain.IsCompleted);
        Assert.False(player.Disposed);
        player.CleanupAllowed.SetResult();
        await drain.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(player.Disposed);
        Assert.True(player.Cleaned);
        Assert.Equal(SpeechPlaybackState.Stopped, (await operation).State);
        await plugin.DisposeAsync();
        Assert.Equal(1, player.Disposals);
    }

    private sealed class Settings : ISpeechSettingsStore
    {
        public SpeechSettings Load() => new(new SpeechConnectionSettings { Enabled = true });
        public void Save(SpeechSettings settings) { }
    }
    private sealed class TestProvider : ISpeechSynthesizer
    {
        public SpeechProviderKind Provider => SpeechProviderKind.OpenAiCompatible;
        public bool Hold { get; init; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<SpeechSynthesisResult> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<SpeechSynthesisResult> SynthesizeAsync(SpeechSynthesisRequest request, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            return Hold ? Result.Task : Task.FromResult(new SpeechSynthesisResult("RIFF0000WAVE"u8.ToArray()));
        }
    }
    private sealed class Player : ISpeechAudioPlayer
    {
        public int Plays { get; private set; }
        public bool HoldCleanup { get; init; }
        public bool Disposed => Disposals != 0;
        public int Disposals { get; private set; }
        public bool Cleaned { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cleaning { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CleanupAllowed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task PlayAsync(SpeechSynthesisResult audio, SpeechPlaybackOptions options, CancellationToken cancellationToken = default)
        {
            Plays++;
            Started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            finally
            {
                Cleaning.TrySetResult();
                if (HoldCleanup) await CleanupAllowed.Task;
                Cleaned = true;
            }
        }
        public void Stop() { }
        public void Dispose() { Assert.True(!HoldCleanup || Cleaned); Disposals++; }
    }
}
