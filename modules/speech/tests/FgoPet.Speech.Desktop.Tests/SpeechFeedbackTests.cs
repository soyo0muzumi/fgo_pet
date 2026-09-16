using System.IO;
using System.Text;
using FgoPet.App.Settings;
using FgoPet.App.Speech;
using FgoPet.Core.Settings;
using FgoPet.Core.Speech;
using FgoPet.Core.Secrets;
using Xunit;

namespace FgoPet.Speech.Desktop.Tests;

public sealed class SpeechFeedbackTests
{
    [Fact]
    public async Task Stopping_before_a_late_result_never_plays_or_reports_completed()
    {
        var synth = new LateSynth();
        var player = new Player();
        using var synthesis = new SpeechSynthesisCoordinator(synth);
        using var playback = new SpeechPlaybackCoordinator(synthesis, player, new Settings());
        var task = playback.PlayAsync(new("test", SpeechProviderKind.IndexTts, new Uri("http://127.0.0.1:7860")), "test", new());
        await synth.Started.Task;
        playback.Stop();
        synth.Result.SetResult(new(Wave));
        var result = await task;
        Assert.False(result.Completed);
        Assert.Equal(SpeechPlaybackState.Stopped, result.State);
        Assert.Equal(0, player.Plays);
    }

    [Fact]
    public async Task Import_keeps_managed_copy_and_does_not_save_other_unsaved_options()
    {
        var root = Path.Combine(Path.GetTempPath(), "fgo-voice-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "source.wav");
        await File.WriteAllBytesAsync(source, Wave);
        try
        {
            var settings = new Settings();
            using var synthesis = new SpeechSynthesisCoordinator(new LateSynth());
            using var playback = new SpeechPlaybackCoordinator(synthesis, new Player(), settings);
            var vm = new SpeechConnectionViewModel(settings, new Credentials(), playback, Path.Combine(root, "voices"))
                { VoiceName = "Test voice", Enabled = true, Provider = SpeechProviderKind.IndexTts };
            await vm.ImportVoiceAsync(source);
            var voice = Assert.Single(settings.Value.SpeechConnection.IndexTtsVoices);
            Assert.False(settings.Value.SpeechConnection.Enabled);
            File.Delete(source);
            Assert.True(File.Exists(voice.AudioPath));
            Assert.Equal(Wave, await File.ReadAllBytesAsync(voice.AudioPath));
            vm.DeleteSelectedVoice();
            Assert.False(File.Exists(voice.AudioPath));
            Assert.Empty(settings.Value.SpeechConnection.IndexTtsVoices);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Removing_voice_never_deletes_a_path_outside_the_managed_directory()
    {
        var root = Path.Combine(Path.GetTempPath(), "fgo-voice-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var outside = Path.Combine(root, "original.wav");
        File.WriteAllBytes(outside, Wave);
        try
        {
            var voice = new ReferenceVoice(Guid.NewGuid().ToString("N"), "outside", outside);
            var settings = new Settings { Value = AppSettings.Defaults with { SpeechConnection = new() { IndexTtsVoiceId = voice.Id, IndexTtsVoices = new[] { voice } } } };
            using var synthesis = new SpeechSynthesisCoordinator(new LateSynth());
            using var playback = new SpeechPlaybackCoordinator(synthesis, new Player(), settings);
            var vm = new SpeechConnectionViewModel(settings, new Credentials(), playback, Path.Combine(root, "voices"));
            vm.DeleteSelectedVoice();
            Assert.True(File.Exists(outside));
            Assert.Single(settings.Value.SpeechConnection.IndexTtsVoices);
            Assert.NotEmpty(vm.ErrorText);
        }
        finally { Directory.Delete(root, true); }
    }

    private static byte[] Wave => Encoding.ASCII.GetBytes("RIFF0000WAVE");

    private sealed class Settings : IAppSettingsStore
    {
        public string Location => "memory";
        public AppSettings Value { get; set; } = AppSettings.Defaults;
        public AppSettings Load() => Value;
        public void Save(AppSettings settings) => Value = settings;
    }

    private sealed class Credentials : ICredentialStore
    {
        public Task SaveAsync(string target, string secret, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool> ExistsAsync(string target, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task DeleteAsync(string target, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class LateSynth : ISpeechSynthesizer
    {
        public SpeechProviderKind Provider => SpeechProviderKind.IndexTts;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<SpeechSynthesisResult> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<SpeechSynthesisResult> SynthesizeAsync(SpeechSynthesisRequest request, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            return Result.Task;
        }
    }

    private sealed class Player : ISpeechAudioPlayer
    {
        public int Plays { get; private set; }
        public Task PlayAsync(SpeechSynthesisResult audio, SpeechPlaybackOptions options, CancellationToken cancellationToken = default)
        {
            Plays++;
            return Task.CompletedTask;
        }
        public void Stop() { }
        public void Dispose() { }
    }
}
