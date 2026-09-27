using FgoPet.App.Speech;
using FgoPet.Core.Speech;
using Xunit;

namespace FgoPet.Speech.Core.Tests;

public sealed class ConfiguredSpeechPlaybackContractTests
{
    [Fact]
    public void Playback_port_and_result_are_available_without_desktop_implementation()
    {
        var assembly = typeof(SpeechPlaybackState).Assembly;
        Assert.Same(assembly, typeof(IConfiguredSpeechPlayback).Assembly);
        Assert.Same(assembly, typeof(SpeechPlaybackResult).Assembly);
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference =>
            reference.Name is "FgoPet.Speech.Desktop" or "FgoPet.Speech.Infrastructure"
                or "FgoPet.Dialogue" or "PresentationFramework" or "FgoPet.App");
    }

    [Fact]
    public void Consumer_port_exposes_only_configured_playback_not_synthesis_settings_or_disposal()
    {
        var type = typeof(IConfiguredSpeechPlayback);
        Assert.Equal(new[] { "PlayConfiguredAsync", "Stop" }, type.GetMethods()
            .Where(method => !method.IsSpecialName).Select(method => method.Name).Order().ToArray());
        var property = Assert.Single(type.GetProperties());
        Assert.Equal("State", property.Name);
        Assert.Equal(typeof(SpeechPlaybackState), property.PropertyType);
        Assert.False(property.CanWrite);
        Assert.Equal("StateChanged", Assert.Single(type.GetEvents()).Name);
        Assert.DoesNotContain(typeof(IDisposable), type.GetInterfaces());
        var method = type.GetMethod("PlayConfiguredAsync")!;
        Assert.Equal(typeof(Task<SpeechPlaybackResult>), method.ReturnType);
        Assert.Equal(new[] { typeof(string), typeof(bool), typeof(CancellationToken) },
            method.GetParameters().Select(parameter => parameter.ParameterType).ToArray());
    }

    [Fact]
    public void Playback_result_retains_its_existing_value_and_copy_semantics()
    {
        var original = new SpeechPlaybackResult(false, SpeechPlaybackState.Failed, "朗读失败，可重试。");
        Assert.False(original.Completed);
        Assert.Equal(SpeechPlaybackState.Failed, original.State);
        var completed = original with { Completed = true, State = SpeechPlaybackState.Ended, SafeError = null };
        Assert.Equal(new SpeechPlaybackResult(true, SpeechPlaybackState.Ended), completed);
        Assert.Equal("朗读失败，可重试。", original.SafeError);
    }
}
