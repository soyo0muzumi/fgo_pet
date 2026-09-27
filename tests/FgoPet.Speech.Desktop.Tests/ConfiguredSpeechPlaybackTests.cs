using System.Text;
using FgoPet.App.Speech;
using FgoPet.Core.Speech;
using FgoPet.Speech.Settings;
using Xunit;

namespace FgoPet.Speech.Desktop.Tests;

public sealed class ConfiguredSpeechPlaybackTests
{
    [Theory]
    [InlineData(false, false, "正文", "自动朗读未启用。")]
    [InlineData(true, true, "正文", "免打扰已开启。")]
    [InlineData(true, false, "正文超过三个字符", "回复较长，请手动朗读。")]
    public async Task Auto_read_policy_remains_owned_by_the_real_coordinator(
        bool autoEnabled, bool dnd, string text, string expected)
    {
        var synth = new Synth();
        using var synthesis = new SpeechSynthesisCoordinator(synth);
        var player = new Player();
        using var owner = new SpeechPlaybackCoordinator(synthesis, player, new Settings(new SpeechConnectionSettings
        {
            Enabled = true, AutoReadEnabled = autoEnabled, DoNotDisturb = dnd, AutoReadLimit = 3,
        }));
        IConfiguredSpeechPlayback port = owner;
        var result = await port.PlayConfiguredAsync(text, autoRead: true);
        Assert.Equal(expected, result.SafeError);
        Assert.Equal(SpeechPlaybackState.Idle, result.State);
        Assert.Equal(0, synth.Calls);
        Assert.Equal(0, player.Plays);
    }

    [Fact]
    public async Task Manual_read_still_works_with_auto_read_disabled_and_reports_states()
    {
        var synth = new Synth();
        using var synthesis = new SpeechSynthesisCoordinator(synth);
        var player = new Player();
        using var owner = new SpeechPlaybackCoordinator(synthesis, player, new Settings(new SpeechConnectionSettings
        {
            Enabled = true, AutoReadEnabled = false, DoNotDisturb = true,
        }));
        IConfiguredSpeechPlayback port = owner;
        var states = new List<SpeechPlaybackState>();
        port.StateChanged += (_, _) => states.Add(port.State);
        var result = await port.PlayConfiguredAsync("手动朗读。");
        Assert.True(result.Completed);
        Assert.Equal(SpeechPlaybackState.Ended, port.State);
        Assert.Equal(new[] { SpeechPlaybackState.Preparing, SpeechPlaybackState.Playing, SpeechPlaybackState.Ended }, states);
        Assert.Equal(1, synth.Calls);
        Assert.Equal(1, player.Plays);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stop_or_caller_cancellation_rejects_a_late_audio_result(bool cancelCaller)
    {
        var synth = new Synth { Hold = true };
        using var synthesis = new SpeechSynthesisCoordinator(synth);
        var player = new Player();
        using var owner = new SpeechPlaybackCoordinator(synthesis, player, new Settings(new SpeechConnectionSettings { Enabled = true }));
        using var cancellation = new CancellationTokenSource();
        IConfiguredSpeechPlayback port = owner;
        var task = port.PlayConfiguredAsync("朗读正文。", cancellationToken: cancellation.Token);
        await synth.Started.Task;
        if (cancelCaller) cancellation.Cancel();
        else port.Stop();
        synth.Result.SetResult(new(Wave));
        var result = await task;
        Assert.False(result.Completed);
        Assert.Equal(SpeechPlaybackState.Stopped, result.State);
        Assert.Equal(0, player.Plays);
    }

    [Fact]
    public async Task Configuration_failure_does_not_invoke_the_synthesizer_or_another_provider()
    {
        var synth = new Synth();
        using var synthesis = new SpeechSynthesisCoordinator(synth);
        var player = new Player();
        using var owner = new SpeechPlaybackCoordinator(synthesis, player, new Settings(SpeechConnectionSettings.Defaults));
        IConfiguredSpeechPlayback port = owner;
        var result = await port.PlayConfiguredAsync("正文");
        Assert.False(result.Completed);
        Assert.Equal(SpeechPlaybackState.Failed, result.State);
        Assert.Contains("朗读设置", result.SafeError!);
        Assert.Equal(0, synth.Calls);
        Assert.Equal(0, player.Plays);
    }

    private static byte[] Wave => Encoding.ASCII.GetBytes("RIFF0000WAVE");
    private sealed class Settings(SpeechConnectionSettings connection) : ISpeechSettingsStore
    {
        private SpeechSettings _value = new(connection);
        public SpeechSettings Load() => _value;
        public void Save(SpeechSettings settings) => _value = settings;
    }
    private sealed class Synth : ISpeechSynthesizer
    {
        public SpeechProviderKind Provider => SpeechProviderKind.OpenAiCompatible;
        public int Calls { get; private set; }
        public bool Hold { get; init; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<SpeechSynthesisResult> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<SpeechSynthesisResult> SynthesizeAsync(SpeechSynthesisRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            Started.TrySetResult();
            return Hold ? Result.Task : Task.FromResult(new SpeechSynthesisResult(Wave));
        }
    }
    private sealed class Player : ISpeechAudioPlayer
    {
        public int Plays { get; private set; }
        public Task PlayAsync(SpeechSynthesisResult audio, SpeechPlaybackOptions options, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Plays++;
            return Task.CompletedTask;
        }
        public void Stop() { }
        public void Dispose() { }
    }
}
