using FgoPet.Core.Speech;
using Xunit;

namespace FgoPet.Speech.Core.Tests;

public sealed class SpeechContractTests
{
    [Fact]
    public void Normalize_bounds_playback_and_auto_read_settings()
    {
        var settings = new SpeechConnectionSettings
        {
            AutoReadLimit = 999,
            Rate = double.NaN,
            Volume = 2,
        }.Normalize();

        Assert.Equal(300, settings.AutoReadLimit);
        Assert.Equal(1.0, settings.Rate);
        Assert.Equal(1.0, settings.Volume);
    }

    [Fact]
    public void Synthesis_result_rejects_non_wave_payloads()
    {
        Assert.Throws<ArgumentException>(() => new SpeechSynthesisResult([1, 2, 3, 4]));
    }
}