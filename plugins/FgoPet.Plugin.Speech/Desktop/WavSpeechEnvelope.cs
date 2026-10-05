using System.Buffers.Binary;

namespace FgoPet.App.Speech;

/// <summary>Bounded 20 ms energy frames; keeps neither audio nor speech text.</summary>
public sealed class WavSpeechEnvelope
{
    private readonly double[] _levels;
    private readonly double _frameSeconds;
    private readonly double _duration;
    private WavSpeechEnvelope(double[] levels, double frameSeconds, double duration)
        => (_levels, _frameSeconds, _duration) = (levels, frameSeconds, duration);
    private static readonly WavSpeechEnvelope Empty = new([], .02, 0);
    public bool IsSupported => _levels.Length > 0;

    public double Sample(TimeSpan position)
    {
        var time = position.TotalSeconds;
        if (time < 0 || time >= _duration || _levels.Length == 0) return 0;
        return _levels[Math.Min(_levels.Length - 1, (int)(time / _frameSeconds))];
    }

    public static WavSpeechEnvelope Create(ReadOnlySpan<byte> wav)
    {
        if (wav.Length < 44 || wav.Length > 64 * 1024 * 1024 || !wav[..4].SequenceEqual("RIFF"u8)
            || !wav.Slice(8, 4).SequenceEqual("WAVE"u8)) return Empty;
        var declared = (long)BinaryPrimitives.ReadUInt32LittleEndian(wav.Slice(4, 4)) + 8;
        if (declared > wav.Length || declared < 44) return Empty;
        ReadOnlySpan<byte> format = default, data = default;
        for (var offset = 12; offset + 8 <= declared;)
        {
            var length = BinaryPrimitives.ReadUInt32LittleEndian(wav.Slice(offset + 4, 4));
            var end = (long)offset + 8 + length;
            if (end > declared) return Empty;
            var chunk = wav.Slice(offset + 8, (int)length);
            if (wav.Slice(offset, 4).SequenceEqual("fmt "u8)) format = chunk;
            else if (wav.Slice(offset, 4).SequenceEqual("data"u8)) data = chunk;
            offset = (int)(end + (length & 1));
        }
        if (format.Length < 16 || data.IsEmpty) return Empty;
        var encoding = BinaryPrimitives.ReadUInt16LittleEndian(format);
        var channels = BinaryPrimitives.ReadUInt16LittleEndian(format.Slice(2));
        var rate = BinaryPrimitives.ReadUInt32LittleEndian(format.Slice(4));
        var align = BinaryPrimitives.ReadUInt16LittleEndian(format.Slice(12));
        var bits = BinaryPrimitives.ReadUInt16LittleEndian(format.Slice(14));
        if (channels is < 1 or > 8 || rate is < 8000 or > 192000
            || (encoding != 1 && encoding != 3)
            || (encoding == 1 && bits is not (8 or 16 or 24 or 32))
            || (encoding == 3 && bits is not (32 or 64))
            || align != channels * bits / 8 || data.Length % align != 0) return Empty;
        var frames = data.Length / align;
        var duration = frames / (double)rate;
        if (duration > 600) return Empty;
        var window = Math.Max(1, (int)(rate / 50));
        var levels = new double[(frames + window - 1) / window];
        var bytesPerSample = bits / 8;
        double peak = 0;
        for (var i = 0; i < levels.Length; i++)
        {
            var first = i * window;
            var count = Math.Min(window, frames - first);
            double sum = 0;
            for (var sample = first * channels; sample < (first + count) * channels; sample++)
            {
                var bytes = data.Slice(sample * bytesPerSample, bytesPerSample);
                double value;
                if (encoding == 3) value = bits == 32
                    ? BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes))
                    : BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(bytes));
                else value = bits switch
                {
                    8 => (bytes[0] - 128) / 128.0,
                    16 => BinaryPrimitives.ReadInt16LittleEndian(bytes) / 32768.0,
                    24 => ((bytes[0] | bytes[1] << 8 | bytes[2] << 16) << 8 >> 8) / 8388608.0,
                    _ => BinaryPrimitives.ReadInt32LittleEndian(bytes) / 2147483648.0,
                };
                if (!double.IsFinite(value)) continue;
                value = Math.Clamp(value, -1, 1);
                sum += value * value;
            }
            levels[i] = Math.Sqrt(sum / (count * channels));
            peak = Math.Max(peak, levels[i]);
        }
        var scale = Math.Max(.08, peak);
        for (var i = 0; i < levels.Length; i++)
            levels[i] = Math.Sqrt(Math.Clamp((levels[i] - .008) / scale, 0, 1));
        return new(levels, window / (double)rate, duration);
    }
}
