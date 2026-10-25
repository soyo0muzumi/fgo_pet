using System.IO;
using System.Buffers.Binary;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using Xunit;

namespace FgoPet.Windows.Tests.Dialogue;

public sealed class SelectedImageDecoderTests
{
    [Theory]
    [InlineData(".png")]
    [InlineData(".jpg")]
    public async Task Actual_decoder_materializes_synthetic_pixels_as_metadata_free_png(string extension)
    {
        await StaRunner.RunAsync(() =>
        {
            var source = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgr24, null,
                new byte[] { 0,0,255, 0,255,0, 255,0,0, 255,255,255 }, 6);
            BitmapEncoder encoder = extension == ".png" ? new PngBitmapEncoder() : new JpegBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var stream = new MemoryStream(); encoder.Save(stream);
            var encoded = stream.ToArray();
            var secret = Encoding.ASCII.GetBytes("private-synthetic-metadata");
            byte[] decorated;
            if (extension == ".png")
                decorated = encoded[..33].Concat(PngTextChunk(secret)).Concat(encoded[33..]).ToArray();
            else
            {
                var metadata = new byte[secret.Length + 10];
                metadata[0] = 0xff; metadata[1] = 0xe1;
                BinaryPrimitives.WriteUInt16BigEndian(metadata.AsSpan(2), (ushort)(metadata.Length - 2));
                Encoding.ASCII.GetBytes("Exif\0\0").CopyTo(metadata, 4); secret.CopyTo(metadata, 10);
                decorated = encoded[..2].Concat(metadata).Concat(encoded[2..]).ToArray();
            }
            var image = SelectedImageDecoder.Decode(decorated, extension, default);
            Assert.Equal(2, image.Width); Assert.Equal(2, image.Height);
            Assert.StartsWith("data:image/png;base64,", image.ToDataUrl());
            Assert.DoesNotContain("private-synthetic-metadata", Encoding.UTF8.GetString(Convert.FromBase64String(image.ToDataUrl().Split(',')[1])));
            return Task.CompletedTask;
        });
    }
    [Fact]
    public async Task Valid_header_without_decodable_pixels_and_cancelled_input_are_rejected()
    {
        await StaRunner.RunAsync(() =>
        {
            var bytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
            Assert.Throws<SelectedImageException>(() => SelectedImageDecoder.Decode(bytes[..33], ".png", default));
            Assert.Throws<OperationCanceledException>(() => SelectedImageDecoder.Decode(bytes, ".png", new(true)));
            return Task.CompletedTask;
        });
    }
    private static byte[] PngTextChunk(byte[] privateText)
    {
        var data = Encoding.ASCII.GetBytes("Description\0").Concat(privateText).ToArray();
        var chunk = new byte[data.Length + 12];
        BinaryPrimitives.WriteUInt32BigEndian(chunk, (uint)data.Length);
        Encoding.ASCII.GetBytes("tEXt").CopyTo(chunk, 4); data.CopyTo(chunk, 8);
        uint crc = uint.MaxValue;
        foreach (var value in chunk.AsSpan(4, data.Length + 4))
        {
            crc ^= value;
            for (var i = 0; i < 8; i++) crc = (crc & 1) != 0 ? 0xedb88320 ^ (crc >> 1) : crc >> 1;
        }
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(chunk.Length - 4), ~crc);
        return chunk;
    }

}
