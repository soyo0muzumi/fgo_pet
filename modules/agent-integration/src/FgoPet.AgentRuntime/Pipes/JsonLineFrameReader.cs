using System.Buffers;
using System.Text;

namespace FgoPet.AgentRuntime.Pipes;

/// <summary>One reader per connection; preserves bytes belonging to the next frame.</summary>
public sealed class JsonLineFrameReader
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly Stream _stream;
    private readonly int _maxFrameBytes;
    private readonly byte[] _buffer = new byte[4096];
    private int _offset;
    private int _count;

    public JsonLineFrameReader(Stream stream, int maxFrameBytes = JsonLinePipeClient.MaxFrameBytes)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        if (maxFrameBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxFrameBytes));
        _maxFrameBytes = maxFrameBytes;
    }

    public async Task<string?> ReadAsync(CancellationToken cancellationToken = default)
    {
        var frame = new ArrayBufferWriter<byte>(Math.Min(4096, _maxFrameBytes));
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_offset == _count)
            {
                _count = await _stream.ReadAsync(_buffer, cancellationToken).ConfigureAwait(false);
                _offset = 0;
                if (_count == 0)
                {
                    if (frame.WrittenCount == 0) return null;
                    throw new EndOfStreamException("The pipe closed before a complete JSON-line frame was received.");
                }
            }

            var newline = Array.IndexOf(_buffer, (byte)'\n', _offset, _count - _offset);
            var length = (newline >= 0 ? newline : _count) - _offset;
            if (length > _maxFrameBytes - frame.WrittenCount)
            {
                throw new InvalidDataException("The JSON frame exceeds the configured byte limit.");
            }

            _buffer.AsSpan(_offset, length).CopyTo(frame.GetSpan(length));
            frame.Advance(length);
            _offset += length;
            if (newline < 0) continue;

            _offset++; // Keep all bytes after this newline for the next ReadAsync call.
            var text = StrictUtf8.GetString(frame.WrittenSpan);
            return text.EndsWith('\r') ? text[..^1] : text;
        }
    }
}
