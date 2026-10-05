using System.Text;
using System.IO;
using FgoPet.App.Speech;
using Xunit;

namespace FgoPet.Speech.Desktop.Tests;

public sealed class WavSpeechEnvelopeTests
{
    [Fact]
    public void Pcm_energy_follows_audio_time_and_silence_without_storing_audio()
    {
        var samples = new short[1600];
        for (var i = 320; i < 960; i++) samples[i] = (short)(Math.Sin(i * .2) * 12000);
        var envelope = WavSpeechEnvelope.Create(Wave(samples));
        Assert.Equal(0, envelope.Sample(TimeSpan.FromMilliseconds(10)));
        Assert.InRange(envelope.Sample(TimeSpan.FromMilliseconds(70)), .8, 1);
        Assert.Equal(0, envelope.Sample(TimeSpan.FromMilliseconds(150)));
        Assert.Equal(0, envelope.Sample(TimeSpan.FromSeconds(1)));
        Assert.Equal(0, envelope.Sample(TimeSpan.FromSeconds(-1)));
    }

    [Theory]
    [InlineData(8)] [InlineData(16)] [InlineData(24)] [InlineData(32)]
    public void Pcm_formats_and_opposite_phase_stereo_retain_energy(int bits)
    {
        var envelope = WavSpeechEnvelope.Create(Wave(Enumerable.Repeat((short)14000, 640).ToArray(), bits, 2));
        Assert.InRange(envelope.Sample(TimeSpan.FromMilliseconds(30)), .9, 1);
    }

    [Fact]
    public void Float_and_nonfinite_samples_are_bounded()
    {
        var wav = Wave(Enumerable.Repeat((short)14000, 320).ToArray(), 32, 1, floating: true);
        var envelope = WavSpeechEnvelope.Create(wav);
        Assert.InRange(envelope.Sample(TimeSpan.FromMilliseconds(10)), .9, 1);
        for (var i = 44; i < wav.Length; i += 4) BitConverter.GetBytes(float.NaN).CopyTo(wav, i);
        Assert.Equal(0, WavSpeechEnvelope.Create(wav).Sample(TimeSpan.Zero));
    }

    [Fact]
    public void Unsupported_and_truncated_wav_disable_animation()
    {
        Assert.Equal(0, WavSpeechEnvelope.Create([1, 2, 3]).Sample(TimeSpan.Zero));
        var wav = Wave(new short[320]);
        Assert.Equal(0, WavSpeechEnvelope.Create(wav[..^1]).Sample(TimeSpan.Zero));
        wav[20] = 7;
        Assert.Equal(0, WavSpeechEnvelope.Create(wav).Sample(TimeSpan.Zero));
    }

    private static byte[] Wave(short[] samples, int bits = 16, int channels = 1, bool floating = false)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        var size = samples.Length * channels * bits / 8;
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + size);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
        writer.Write((short)(floating ? 3 : 1)); writer.Write((short)channels); writer.Write(8000);
        writer.Write(8000 * channels * bits / 8); writer.Write((short)(channels * bits / 8)); writer.Write((short)bits);
        writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(size);
        foreach (var sample in samples)
            for (var channel = 0; channel < channels; channel++)
            {
                var value = channel == 0 ? sample : -sample;
                if (floating) writer.Write(value / 32768f);
                else if (bits == 8) writer.Write((byte)((value >> 8) + 128));
                else if (bits == 16) writer.Write((short)value);
                else if (bits == 24) { var v = value << 8; writer.Write((byte)v); writer.Write((byte)(v >> 8)); writer.Write((byte)(v >> 16)); }
                else writer.Write(value << 16);
            }
        return stream.ToArray();
    }
}
