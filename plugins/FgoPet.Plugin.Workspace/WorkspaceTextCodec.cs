using System.Security.Cryptography;
using System.Text;

namespace FgoPet.Plugin.Workspace;

/// <summary>Strict decoded workspace text with the source encoding and file fingerprint.</summary>
public sealed record WorkspaceText(string Content, string Encoding, byte[] Preamble, string Version);

/// <summary>Decode only bounded UTF-8 and BOM-marked UTF-16 workspace text.</summary>
public static class WorkspaceTextCodec
{
    public const int MaxInputBytes = 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly UnicodeEncoding Utf16Le = new(false, false, true);
    private static readonly UnicodeEncoding Utf16Be = new(true, false, true);
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];
    private static readonly byte[] Utf16LeBom = [0xFF, 0xFE];
    private static readonly byte[] Utf16BeBom = [0xFE, 0xFF];

    public static WorkspaceText Decode(byte[] bytes)
        => DecodeBounded(bytes, MaxInputBytes);

    internal static WorkspaceText DecodeBounded(byte[] bytes, int maximumInputBytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (maximumInputBytes < 0 || maximumInputBytes > 8 * MaxInputBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumInputBytes));
        if (bytes.Length > maximumInputBytes) throw new WorkspaceTextDecodeException("WORKSPACE_FILE_TOO_LARGE");

        string encodingName;
        Encoding encoding;
        byte[] preamble;
        int offset;
        if (StartsWith(bytes, [0x00, 0x00, 0xFE, 0xFF]) || StartsWith(bytes, [0xFF, 0xFE, 0x00, 0x00]))
            throw new WorkspaceTextDecodeException("WORKSPACE_BINARY_OR_UNSUPPORTED_ENCODING");
        if (StartsWith(bytes, Utf8Bom))
        {
            encodingName = "utf-8";
            encoding = Utf8;
            preamble = Utf8Bom.ToArray();
            offset = Utf8Bom.Length;
        }
        else if (StartsWith(bytes, Utf16LeBom))
        {
            encodingName = "utf-16le";
            encoding = Utf16Le;
            preamble = Utf16LeBom.ToArray();
            offset = Utf16LeBom.Length;
        }
        else if (StartsWith(bytes, Utf16BeBom))
        {
            encodingName = "utf-16be";
            encoding = Utf16Be;
            preamble = Utf16BeBom.ToArray();
            offset = Utf16BeBom.Length;
        }
        else
        {
            encodingName = "utf-8";
            encoding = Utf8;
            preamble = [];
            offset = 0;
        }

        string content;
        try { content = encoding.GetString(bytes, offset, bytes.Length - offset); }
        catch (DecoderFallbackException) { throw new WorkspaceTextDecodeException("WORKSPACE_BINARY_OR_UNSUPPORTED_ENCODING"); }
        EnsureText(content);
        return new(content, encodingName, preamble, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    /// <summary>Encode replacement text using the original supported encoding and exact BOM choice.</summary>
    public static byte[] Encode(WorkspaceText original, string content)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(content);
        if (original.Preamble is null)
            throw new WorkspaceTextDecodeException("WORKSPACE_BINARY_OR_UNSUPPORTED_ENCODING");

        Encoding encoding;
        ReadOnlySpan<byte> expectedPreamble;
        switch (original.Encoding)
        {
            case "utf-8":
                encoding = Utf8;
                if (original.Preamble.AsSpan().SequenceEqual(Utf8Bom)) expectedPreamble = Utf8Bom;
                else if (original.Preamble.Length == 0) expectedPreamble = ReadOnlySpan<byte>.Empty;
                else throw new WorkspaceTextDecodeException("WORKSPACE_BINARY_OR_UNSUPPORTED_ENCODING");
                break;
            case "utf-16le":
                encoding = Utf16Le;
                expectedPreamble = Utf16LeBom;
                break;
            case "utf-16be":
                encoding = Utf16Be;
                expectedPreamble = Utf16BeBom;
                break;
            default:
                throw new WorkspaceTextDecodeException("WORKSPACE_BINARY_OR_UNSUPPORTED_ENCODING");
        }

        if (original.Preamble is null || !original.Preamble.AsSpan().SequenceEqual(expectedPreamble))
            throw new WorkspaceTextDecodeException("WORKSPACE_BINARY_OR_UNSUPPORTED_ENCODING");
        EnsureText(content);
        byte[] body;
        try { body = encoding.GetBytes(content); }
        catch (EncoderFallbackException) { throw new WorkspaceTextDecodeException("WORKSPACE_BINARY_OR_UNSUPPORTED_ENCODING"); }
        if (body.Length + original.Preamble.Length > MaxInputBytes)
            throw new WorkspaceTextDecodeException("WORKSPACE_OUTPUT_TOO_LARGE");

        var output = new byte[original.Preamble.Length + body.Length];
        original.Preamble.CopyTo(output, 0);
        body.CopyTo(output, original.Preamble.Length);
        return output;
    }

    private static bool StartsWith(byte[] bytes, ReadOnlySpan<byte> prefix) => bytes.AsSpan().StartsWith(prefix);

    private static void EnsureText(string content)
    {
        foreach (var rune in content.EnumerateRunes())
            if (Rune.IsControl(rune) && rune.Value is not ('\t' or '\n' or '\r'))
                throw new WorkspaceTextDecodeException("WORKSPACE_BINARY_OR_UNSUPPORTED_ENCODING");
    }
}

public sealed class WorkspaceTextDecodeException(string code) : InvalidOperationException(code)
{
    public string Code { get; } = code;
}
