using System.IO;
using System.Text;
using FgoPet.App.Settings;
using FgoPet.App.Speech;
using FgoPet.Core.Speech;
using FgoPet.Core.Secrets;
using FgoPet.Speech.Settings;
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
        using var playback = new SpeechPlaybackCoordinator(synthesis, player, new Settings(SpeechSettings.Defaults));
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
            var settings = new Settings(SpeechSettings.Defaults);
            using var synthesis = new SpeechSynthesisCoordinator(new LateSynth());
            using var playback = new SpeechPlaybackCoordinator(synthesis, new Player(), settings);
            var vm = new SpeechConnectionViewModel(settings, new Credentials(), playback, Path.Combine(root, "voices"))
                { VoiceName = "Test voice", Enabled = true, Provider = SpeechProviderKind.IndexTts };
            await vm.ImportVoiceAsync(source);
            var voice = Assert.Single(settings.Current.Connection.IndexTtsVoices);
            Assert.False(settings.Current.Connection.Enabled);
            File.Delete(source);
            Assert.True(File.Exists(voice.AudioPath));
            Assert.Equal(Wave, await File.ReadAllBytesAsync(voice.AudioPath));
            vm.DeleteSelectedVoice();
            Assert.False(File.Exists(voice.AudioPath));
            Assert.Empty(settings.Current.Connection.IndexTtsVoices);
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
            var settings = new Settings(SpeechSettings.Defaults with
            {
                Connection = new() { IndexTtsVoiceId = voice.Id, IndexTtsVoices = new[] { voice } },
            });
            using var synthesis = new SpeechSynthesisCoordinator(new LateSynth());
            using var playback = new SpeechPlaybackCoordinator(synthesis, new Player(), settings);
            var vm = new SpeechConnectionViewModel(settings, new Credentials(), playback, Path.Combine(root, "voices"));
            vm.DeleteSelectedVoice();
            Assert.True(File.Exists(outside));
            Assert.Single(settings.Current.Connection.IndexTtsVoices);
            Assert.NotEmpty(vm.ErrorText);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Metadata_only_restored_voice_is_shown_but_preview_never_invokes_synthesizer()
    {
        var voice = new ReferenceVoice("restored-voice", "Restored Voice", string.Empty);
        var settings = new Settings(new SpeechSettings(new SpeechConnectionSettings
        {
            Enabled = true,
            Provider = SpeechProviderKind.IndexTts,
            IndexTtsVoiceId = voice.Id,
            IndexTtsVoices = [voice],
        }));
        var synth = new RejectIfCalledSynth();
        using var synthesis = new SpeechSynthesisCoordinator(synth);
        using var playback = new SpeechPlaybackCoordinator(synthesis, new Player(), settings);
        var vm = new SpeechConnectionViewModel(settings, new Credentials(), playback);

        Assert.Equal(voice, Assert.Single(vm.Voices));
        Assert.Equal(voice, vm.SelectedVoice);

        await vm.PreviewCommand.ExecuteAsync(null);

        Assert.Equal(0, synth.Calls);
        Assert.Equal("请先导入并选择参考音色。", vm.ErrorText);
    }

    [Fact]
    public void Deleting_metadata_only_voice_removes_record_without_touching_files()
    {
        var root = Path.Combine(Path.GetTempPath(), "fgo-voice-test-" + Guid.NewGuid().ToString("N"));
        var voiceDirectory = Path.Combine(root, "voices");
        Directory.CreateDirectory(voiceDirectory);
        var selected = new ReferenceVoice(Guid.NewGuid().ToString("N"), "Restored Two", string.Empty);
        var first = new ReferenceVoice("restored-one", "Restored One", string.Empty);
        var last = new ReferenceVoice("restored-three", "Restored Three", string.Empty);
        var unrelatedManagedFile = Path.Combine(voiceDirectory, selected.Id + ".wav");
        File.WriteAllBytes(unrelatedManagedFile, Wave);
        try
        {
            var settings = new Settings(new SpeechSettings(new SpeechConnectionSettings
            {
                Provider = SpeechProviderKind.IndexTts,
                IndexTtsVoiceId = selected.Id,
                IndexTtsVoices = [first, selected, last],
            }));
            using var synthesis = new SpeechSynthesisCoordinator(new LateSynth());
            using var playback = new SpeechPlaybackCoordinator(synthesis, new Player(), settings);
            var vm = new SpeechConnectionViewModel(settings, new Credentials(), playback, voiceDirectory);

            vm.DeleteSelectedVoice();

            Assert.Empty(vm.ErrorText);
            Assert.Null(vm.SelectedVoice);
            Assert.Equal([first, last], vm.Voices);
            Assert.Equal(string.Empty, settings.Current.Connection.IndexTtsVoiceId);
            Assert.Equal([first, last], settings.Current.Connection.IndexTtsVoices);
            Assert.True(File.Exists(unrelatedManagedFile));
            Assert.Equal(Wave, File.ReadAllBytes(unrelatedManagedFile));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Deleting_one_of_32_metadata_only_voices_enables_managed_import()
    {
        var root = Path.Combine(Path.GetTempPath(), "fgo-voice-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "source.wav");
        await File.WriteAllBytesAsync(source, Wave);
        var restored = Enumerable.Range(0, 32)
            .Select(index => new ReferenceVoice($"restored-{index:D2}", $"Restored {index:D2}", string.Empty))
            .ToArray();
        try
        {
            var settings = new Settings(new SpeechSettings(new SpeechConnectionSettings
            {
                Provider = SpeechProviderKind.IndexTts,
                IndexTtsVoiceId = restored[0].Id,
                IndexTtsVoices = restored,
            }));
            using var synthesis = new SpeechSynthesisCoordinator(new LateSynth());
            using var playback = new SpeechPlaybackCoordinator(synthesis, new Player(), settings);
            var vm = new SpeechConnectionViewModel(settings, new Credentials(), playback, Path.Combine(root, "voices"))
            {
                VoiceName = "Replacement Voice",
            };

            vm.DeleteSelectedVoice();
            await vm.ImportVoiceAsync(source);

            Assert.Empty(vm.ErrorText);
            Assert.Equal(32, vm.Voices.Count);
            Assert.Equal(restored.Skip(1), vm.Voices.Take(31));
            var replacement = vm.Voices[^1];
            Assert.Equal("Replacement Voice", replacement.Name);
            Assert.False(string.IsNullOrWhiteSpace(replacement.AudioPath));
            Assert.True(File.Exists(replacement.AudioPath));
            Assert.Equal(replacement, vm.SelectedVoice);
            Assert.Equal(replacement.Id, settings.Current.Connection.IndexTtsVoiceId);
            Assert.Equal(vm.Voices, settings.Current.Connection.IndexTtsVoices);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static byte[] Wave => Encoding.ASCII.GetBytes("RIFF0000WAVE");

    private sealed class Settings(SpeechSettings current) : ISpeechSettingsStore
    {
        public SpeechSettings Current { get; private set; } = current;
        public SpeechSettings Load() => Current;
        public void Save(SpeechSettings settings) => Current = settings;
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

    private sealed class RejectIfCalledSynth : ISpeechSynthesizer
    {
        public SpeechProviderKind Provider => SpeechProviderKind.IndexTts;
        public int Calls { get; private set; }

        public Task<SpeechSynthesisResult> SynthesizeAsync(
            SpeechSynthesisRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("Metadata-only voice must not reach synthesis.");
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
