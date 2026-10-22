using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FgoPet.Core.Dialogue;

namespace FgoPet.App.Dialogue;

/// <summary>Only called after a native file selection. Reads one bounded file and strips metadata by copying pixels.</summary>
internal static class SelectedImageDecoder
{
    internal static async Task<ChatImageInput> ReadAsync(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            if (!Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal))
                throw new SelectedImageException("IMAGE_LOCAL_FILE_REQUIRED");
            var file = new FileInfo(path);
            if ((file.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new SelectedImageException("IMAGE_LOCAL_FILE_REQUIRED");
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, true);
            if (stream.Length is <= 0 or > SelectedImageFormat.MaxSourceBytes) throw new SelectedImageException("IMAGE_SIZE_LIMIT");
            var bytes = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(bytes, token);
            if (stream.ReadByte() != -1) throw new SelectedImageException("IMAGE_SIZE_LIMIT");
            return Decode(bytes, Path.GetExtension(path), token);
        }
        catch (OperationCanceledException) { throw; }
        catch (SelectedImageException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { throw new SelectedImageException("IMAGE_READ_FAILED"); }
    }
    internal static ChatImageInput Decode(byte[] bytes, string extension, CancellationToken token, bool isBrowserImage = false)
    {
        token.ThrowIfCancellationRequested();
        var dimensions = SelectedImageFormat.Inspect(bytes, extension);
        try
        {
            var sanitized = SelectedImageSanitizer.Prepare(bytes, extension);
            token.ThrowIfCancellationRequested();
            using var input = new MemoryStream(sanitized, writable: false);
            var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count != 1) throw new SelectedImageException("IMAGE_TYPE_UNSUPPORTED");
            var frame = decoder.Frames[0];
            if (frame.PixelWidth != dimensions.Width || frame.PixelHeight != dimensions.Height)
                throw new SelectedImageException("IMAGE_INVALID_DATA");
            token.ThrowIfCancellationRequested();
            var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            var stride = checked(dimensions.Width * 4);
            var pixels = new byte[checked(stride * dimensions.Height)];
            converted.CopyPixels(pixels, stride, 0); // Forces full decode; a valid header alone is insufficient.
            token.ThrowIfCancellationRequested();
            var clean = BitmapSource.Create(dimensions.Width, dimensions.Height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(clean));
            using var output = new MemoryStream();
            encoder.Save(output);
            token.ThrowIfCancellationRequested();
            if (output.Length > ChatImageInput.MaxBytes) throw new SelectedImageException("IMAGE_SIZE_LIMIT");
            return new(output.ToArray(), dimensions.Width, dimensions.Height, isBrowserImage);
        }
        catch (OperationCanceledException) { throw; }
        catch (SelectedImageException) { throw; }
        catch (Exception error) when (error is IOException or ArgumentException or NotSupportedException or InvalidOperationException
            or System.Runtime.InteropServices.COMException)
        { throw new SelectedImageException("IMAGE_INVALID_DATA"); }
    }
}
