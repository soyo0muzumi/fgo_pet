using System.Buffers.Binary;

namespace FgoPet.Core.Dialogue;

/// <summary>Removes non-pixel metadata before entering a platform decoder, without decompressing it.</summary>
public static class SelectedImageSanitizer
{
    public static byte[] Prepare(byte[] bytes, string extension)
    {
        _ = SelectedImageFormat.Inspect(bytes, extension);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ? Png(bytes) : Jpeg(bytes);
    }
    private static SelectedImageException Invalid() => new("IMAGE_INVALID_DATA");
    private static byte[] Png(byte[] bytes)
    {
        using var output = new MemoryStream(); output.Write(bytes, 0, 8);
        var offset = 8; var chunks = 0; var hasData = false;
        while (offset < bytes.Length)
        {
            if (++chunks > 4096 || bytes.Length - offset < 12) throw Invalid();
            var size = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset));
            if (size > (uint)(bytes.Length - offset - 12)) throw Invalid();
            var length = (int)size;
            var type = bytes.AsSpan(offset + 4, 4);
            if (type.ToArray().Any(value => value is not (>= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z'))) throw Invalid();
            if (Crc(bytes.AsSpan(offset + 4, length + 4)) != BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset + length + 8))) throw Invalid();
            var header = type.SequenceEqual("IHDR"u8); var end = type.SequenceEqual("IEND"u8);
            var palette = type.SequenceEqual("PLTE"u8); var transparency = type.SequenceEqual("tRNS"u8);
            var data = type.SequenceEqual("IDAT"u8);
            if ((chunks == 1) != header || (header && length != 13) || (palette && (length is < 3 or > 768 || length % 3 != 0))
                || (transparency && length > 256) || (end && (length != 0 || !hasData))) throw Invalid();
            if (type.SequenceEqual("acTL"u8) || type.SequenceEqual("fcTL"u8) || type.SequenceEqual("fdAT"u8))
                throw new SelectedImageException("IMAGE_TYPE_UNSUPPORTED");
            if (!header && !end && !palette && !data && (type[0] & 32) == 0) throw new SelectedImageException("IMAGE_TYPE_UNSUPPORTED");
            if (header || end || palette || transparency || data) output.Write(bytes, offset, length + 12);
            hasData |= data;
            offset += length + 12;
            if (end) return offset == bytes.Length ? output.ToArray() : throw Invalid();
        }
        throw Invalid();
    }
    private static byte[] Jpeg(byte[] bytes)
    {
        using var output = new MemoryStream(); output.Write(bytes, 0, 2);
        var offset = 2; var markers = 0; var frames = 0; var scans = 0;
        while (offset < bytes.Length)
        {
            if (++markers > 4096 || bytes[offset] != 0xff) throw Invalid();
            var start = offset;
            while (offset < bytes.Length && bytes[offset] == 0xff) offset++;
            if (offset >= bytes.Length) throw Invalid();
            var marker = bytes[offset++];
            if (marker == 0xd9)
            {
                if (scans == 0 || offset != bytes.Length) throw Invalid();
                output.Write(bytes, start, offset - start); return output.ToArray();
            }
            if (offset + 2 > bytes.Length) throw Invalid();
            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset));
            if (length < 2 || offset + length > bytes.Length) throw Invalid();
            var metadata = marker is >= 0xe0 and <= 0xef or 0xfe;
            if (!metadata && marker is not (0xc0 or 0xc1 or 0xc2 or 0xc4 or 0xdb or 0xdd or 0xda))
                throw new SelectedImageException("IMAGE_TYPE_UNSUPPORTED");
            if (marker is 0xc0 or 0xc1 or 0xc2)
            {
                if (++frames != 1 || length < 8 || bytes[offset + 7] is not (1 or 3)) throw new SelectedImageException("IMAGE_TYPE_UNSUPPORTED");
            }
            if (marker == 0xee)
            {
                // Adobe's transform flag is coding information, not arbitrary APP metadata.
                // Preserve only its bounded standardized values; CMYK/YCCK is not supported.
                if (length != 14 || !bytes.AsSpan(offset + 2, 5).SequenceEqual("Adobe"u8) || bytes[offset + 13] > 1)
                    throw new SelectedImageException("IMAGE_TYPE_UNSUPPORTED");
                byte[] adobe = [0xff, 0xee, 0, 14, 65, 100, 111, 98, 101, 0, 100, 0, 0, 0, 0, bytes[offset + 13]];
                output.Write(adobe);
            }
            else if (!metadata) output.Write(bytes, start, offset + length - start);
            offset += length;
            if (marker != 0xda) continue;
            if (frames != 1) throw Invalid();
            scans++; var scanStart = offset;
            while (offset < bytes.Length)
            {
                if (bytes[offset] != 0xff) { offset++; continue; }
                var markerStart = offset;
                while (offset < bytes.Length && bytes[offset] == 0xff) offset++;
                if (offset >= bytes.Length) throw Invalid();
                if (bytes[offset] is 0x00 or >= 0xd0 and <= 0xd7) { offset++; continue; }
                output.Write(bytes, scanStart, markerStart - scanStart); offset = markerStart; break;
            }
        }
        throw Invalid();
    }
    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(value =>
    {
        var crc = (uint)value;
        for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? 0xedb88320 ^ (crc >> 1) : crc >> 1;
        return crc;
    }).ToArray();
    private static uint Crc(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes) crc = CrcTable[(crc ^ value) & 255] ^ (crc >> 8);
        return ~crc;
    }
}
