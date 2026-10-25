using System.Buffers.Binary;

namespace FgoPet.Core.Dialogue;

/// <summary>Header bounds are checked before any pixel decoder allocation. Decoding remains mandatory.</summary>
public static class SelectedImageFormat
{
    public const int MaxSourceBytes = 8 * 1024 * 1024;
    public static (int Width, int Height) Inspect(ReadOnlySpan<byte> bytes, string extension)
    {
        if (bytes.Length is < 24 or > MaxSourceBytes) throw new SelectedImageException("IMAGE_SIZE_LIMIT");
        int width, height;
        if (extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
        {
            if (!bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
                || BinaryPrimitives.ReadUInt32BigEndian(bytes[8..]) != 13 || !bytes.Slice(12, 4).SequenceEqual("IHDR"u8))
                throw new SelectedImageException("IMAGE_INVALID_DATA");
            var w = BinaryPrimitives.ReadUInt32BigEndian(bytes[16..]);
            var h = BinaryPrimitives.ReadUInt32BigEndian(bytes[20..]);
            if (w > int.MaxValue || h > int.MaxValue) throw new SelectedImageException("IMAGE_DIMENSION_LIMIT");
            width = (int)w; height = (int)h;
        }
        else if (extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            if (bytes[0] != 0xff || bytes[1] != 0xd8) throw new SelectedImageException("IMAGE_INVALID_DATA");
            width = height = 0;
            var offset = 2;
            while (offset < bytes.Length)
            {
                if (bytes[offset++] != 0xff) throw new SelectedImageException("IMAGE_INVALID_DATA");
                while (offset < bytes.Length && bytes[offset] == 0xff) offset++;
                if (offset >= bytes.Length) break;
                var marker = bytes[offset++];
                if (marker is 0xd9 or 0xda) break;
                if (marker is 0x00 or 0xd8 or >= 0xd0 and <= 0xd7) throw new SelectedImageException("IMAGE_INVALID_DATA");
                if (offset + 2 > bytes.Length) break;
                var length = BinaryPrimitives.ReadUInt16BigEndian(bytes[offset..]);
                if (length < 2 || offset + length > bytes.Length) throw new SelectedImageException("IMAGE_INVALID_DATA");
                if (marker is 0xc0 or 0xc1 or 0xc2)
                {
                    if (length < 8 || bytes[offset + 2] != 8) throw new SelectedImageException("IMAGE_INVALID_DATA");
                    height = BinaryPrimitives.ReadUInt16BigEndian(bytes[(offset + 3)..]);
                    width = BinaryPrimitives.ReadUInt16BigEndian(bytes[(offset + 5)..]);
                    break;
                }
                offset += length;
            }
            if (width == 0 || height == 0) throw new SelectedImageException("IMAGE_INVALID_DATA");
        }
        else throw new SelectedImageException("IMAGE_TYPE_UNSUPPORTED");
        if (width is < 1 or > ChatImageInput.MaxDimension || height is < 1 or > ChatImageInput.MaxDimension
            || (long)width * height > ChatImageInput.MaxPixels) throw new SelectedImageException("IMAGE_DIMENSION_LIMIT");
        return (width, height);
    }
}
