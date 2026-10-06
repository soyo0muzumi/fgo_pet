using System.Diagnostics;
using System.IO.Compression;
using System.Buffers.Binary;
using System.Text;
using System.Xml;

namespace FgoPet.Plugin.Documents;

public sealed class DocumentExtractionException : Exception
{
    public DocumentExtractionException(string code)
        : base(code)
    {
        Code = code;
    }

    public string Code { get; }
}

public static class DocxTextExtractor
{
    private const int MaximumInputBytes = 8 * 1024 * 1024;
    private const int MaximumEntries = 256;
    private const long MaximumArchiveBytes = 16L * 1024 * 1024;
    private const int MaximumDocumentXmlBytes = 4 * 1024 * 1024;
    private const int MaximumOutputBytes = 1024 * 1024;
    private const int BufferSize = 64 * 1024;
    private const string DocumentPartName = "word/document.xml";
    private const string WordNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly uint[] Crc32Table = CreateCrc32Table();

    public static string Extract(byte[] document, CancellationToken token)
    {
        if (document is null)
            throw Error("DOCUMENT_INVALID_ARCHIVE");
        if (document.Length > MaximumInputBytes)
            throw Error("DOCUMENT_INPUT_TOO_LARGE");

        var startedAt = Stopwatch.GetTimestamp();
        CheckBudget(token, startedAt);

        try
        {
            using var input = new MemoryStream(document, writable: false);
            using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
            if (archive.Entries.Count > MaximumEntries)
                throw Error("DOCUMENT_ARCHIVE_TOO_LARGE");

            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ZipArchiveEntry? documentEntry = null;
            long declaredTotal = 0;

            foreach (var entry in archive.Entries)
            {
                CheckBudget(token, startedAt);
                ValidateEntryName(entry.FullName);
                if (!seenNames.Add(entry.FullName))
                    throw Error("DOCUMENT_INVALID_ARCHIVE");

                if (entry.Length < 0 || entry.Length > MaximumArchiveBytes - declaredTotal)
                    throw Error("DOCUMENT_ARCHIVE_TOO_LARGE");
                declaredTotal += entry.Length;

                if (string.Equals(entry.FullName, DocumentPartName, StringComparison.Ordinal))
                {
                    if (documentEntry is not null)
                        throw Error("DOCUMENT_INVALID_ARCHIVE");
                    if (entry.Length > MaximumDocumentXmlBytes)
                        throw Error("DOCUMENT_ARCHIVE_TOO_LARGE");
                    documentEntry = entry;
                }
            }

            if (documentEntry is null)
                throw Error("DOCUMENT_INVALID_ARCHIVE");

            var documentXml = ReadEntriesBounded(document, archive, documentEntry, token, startedAt);
            return ExtractXml(documentXml, token, startedAt);
        }
        catch (DocumentExtractionException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or XmlException or NotSupportedException or ArgumentException)
        {
            throw Error("DOCUMENT_INVALID_ARCHIVE");
        }
    }

    private static byte[] ReadEntriesBounded(
        byte[] archiveBytes,
        ZipArchive archive,
        ZipArchiveEntry documentEntry,
        CancellationToken token,
        long startedAt)
    {
        var rawEntries = ReadRawEntries(archiveBytes, archive, token, startedAt);
        var buffer = new byte[BufferSize];
        using var documentXml = new MemoryStream();
        long actualTotal = 0;

        for (var index = 0; index < rawEntries.Count; index++)
        {
            CheckBudget(token, startedAt);
            var entry = archive.Entries[index];
            var rawEntry = rawEntries[index];
            var isDocument = ReferenceEquals(entry, documentEntry);
            long actualEntry = 0;
            using var compressed = new MemoryStream(
                archiveBytes,
                rawEntry.DataOffset,
                rawEntry.CompressedSize,
                writable: false,
                publiclyVisible: true);
            using var source = rawEntry.CompressionMethod switch
            {
                0 => (Stream)compressed,
                8 => new DeflateStream(compressed, CompressionMode.Decompress, leaveOpen: true),
                _ => throw Error("DOCUMENT_INVALID_ARCHIVE"),
            };
            var crc = uint.MaxValue;

            while (true)
            {
                CheckBudget(token, startedAt);
                var remainingTotal = MaximumArchiveBytes - actualTotal;
                var remainingDocument = isDocument ? MaximumDocumentXmlBytes - actualEntry : remainingTotal;
                var allowed = Math.Min(remainingTotal, remainingDocument);
                var requested = (int)Math.Min(buffer.Length, allowed + 1);
                var read = source.Read(buffer, 0, requested);
                if (read == 0)
                    break;

                if (read > remainingTotal || (isDocument && read > remainingDocument))
                    throw Error("DOCUMENT_ARCHIVE_TOO_LARGE");

                actualTotal += read;
                actualEntry += read;
                crc = UpdateCrc32(crc, buffer.AsSpan(0, read));
                if (isDocument)
                    documentXml.Write(buffer, 0, read);
            }

            if (actualEntry != rawEntry.UncompressedSize || actualEntry != entry.Length ||
                ~crc != rawEntry.Crc32)
                throw Error("DOCUMENT_INVALID_ARCHIVE");
        }

        return documentXml.ToArray();
    }

    private static List<RawEntry> ReadRawEntries(
        byte[] archiveBytes,
        ZipArchive archive,
        CancellationToken token,
        long startedAt)
    {
        var endOffset = FindEndOfCentralDirectory(archiveBytes, token, startedAt);
        if (endOffset < 0)
            throw Error("DOCUMENT_INVALID_ARCHIVE");

        var commentLength = ReadUInt16(archiveBytes, endOffset + 20, archiveBytes.Length);
        var diskNumber = ReadUInt16(archiveBytes, endOffset + 4, archiveBytes.Length);
        var centralDisk = ReadUInt16(archiveBytes, endOffset + 6, archiveBytes.Length);
        var entriesOnDisk = ReadUInt16(archiveBytes, endOffset + 8, archiveBytes.Length);
        var entryCount = ReadUInt16(archiveBytes, endOffset + 10, archiveBytes.Length);
        var centralSize = ReadUInt32(archiveBytes, endOffset + 12, archiveBytes.Length);
        var centralOffset = ReadUInt32(archiveBytes, endOffset + 16, archiveBytes.Length);
        if ((long)endOffset + 22 + commentLength != archiveBytes.Length ||
            diskNumber != 0 || centralDisk != 0 || entriesOnDisk != entryCount ||
            entryCount == ushort.MaxValue || centralSize == uint.MaxValue || centralOffset == uint.MaxValue ||
            entryCount != archive.Entries.Count ||
            (long)centralOffset + centralSize != endOffset)
            throw Error("DOCUMENT_INVALID_ARCHIVE");

        var cursor = checked((int)centralOffset);
        var centralEnd = checked(cursor + (int)centralSize);
        var result = new List<RawEntry>(entryCount);
        for (var index = 0; index < entryCount; index++)
        {
            CheckBudget(token, startedAt);
            EnsureRange(cursor, 46, centralEnd);
            if (ReadUInt32(archiveBytes, cursor, centralEnd) != 0x02014b50)
                throw Error("DOCUMENT_INVALID_ARCHIVE");

            var flags = ReadUInt16(archiveBytes, cursor + 8, centralEnd);
            var method = ReadUInt16(archiveBytes, cursor + 10, centralEnd);
            var crc32 = ReadUInt32(archiveBytes, cursor + 16, centralEnd);
            var compressedSize = ReadUInt32(archiveBytes, cursor + 20, centralEnd);
            var uncompressedSize = ReadUInt32(archiveBytes, cursor + 24, centralEnd);
            var nameLength = ReadUInt16(archiveBytes, cursor + 28, centralEnd);
            var extraLength = ReadUInt16(archiveBytes, cursor + 30, centralEnd);
            var fileCommentLength = ReadUInt16(archiveBytes, cursor + 32, centralEnd);
            var diskStart = ReadUInt16(archiveBytes, cursor + 34, centralEnd);
            var localHeaderOffset = ReadUInt32(archiveBytes, cursor + 42, centralEnd);
            var variableLength = (long)nameLength + extraLength + fileCommentLength;
            if (compressedSize == uint.MaxValue || uncompressedSize == uint.MaxValue ||
                localHeaderOffset == uint.MaxValue || diskStart != 0 ||
                (flags & 0x2041) != 0 || method is not (0 or 8) ||
                variableLength > centralEnd - cursor - 46)
                throw Error("DOCUMENT_INVALID_ARCHIVE");

            var nameOffset = cursor + 46;
            var name = DecodeEntryName(archiveBytes, nameOffset, nameLength, flags);
            if (!string.Equals(name, archive.Entries[index].FullName, StringComparison.Ordinal))
                throw Error("DOCUMENT_INVALID_ARCHIVE");

            var localOffset = checked((int)localHeaderOffset);
            EnsureRange(localOffset, 30, checked((int)centralOffset));
            if (ReadUInt32(archiveBytes, localOffset, checked((int)centralOffset)) != 0x04034b50)
                throw Error("DOCUMENT_INVALID_ARCHIVE");

            var localFlags = ReadUInt16(archiveBytes, localOffset + 6, checked((int)centralOffset));
            var localMethod = ReadUInt16(archiveBytes, localOffset + 8, checked((int)centralOffset));
            var localNameLength = ReadUInt16(archiveBytes, localOffset + 26, checked((int)centralOffset));
            var localExtraLength = ReadUInt16(archiveBytes, localOffset + 28, checked((int)centralOffset));
            var localVariableLength = (long)localNameLength + localExtraLength;
            if (localFlags != flags || localMethod != method ||
                localNameLength != nameLength || localVariableLength > checked((int)centralOffset) - localOffset - 30 ||
                !archiveBytes.AsSpan(localOffset + 30, localNameLength)
                    .SequenceEqual(archiveBytes.AsSpan(nameOffset, nameLength)))
                throw Error("DOCUMENT_INVALID_ARCHIVE");

            var dataOffset = checked(localOffset + 30 + localNameLength + localExtraLength);
            if ((flags & 0x0008) == 0 &&
                (ReadUInt32(archiveBytes, localOffset + 14, checked((int)centralOffset)) != crc32 ||
                 ReadUInt32(archiveBytes, localOffset + 18, checked((int)centralOffset)) != compressedSize ||
                 ReadUInt32(archiveBytes, localOffset + 22, checked((int)centralOffset)) != uncompressedSize))
                throw Error("DOCUMENT_INVALID_ARCHIVE");

            if ((long)dataOffset + compressedSize > centralOffset)
                throw Error("DOCUMENT_INVALID_ARCHIVE");

            result.Add(new RawEntry(
                method,
                crc32,
                checked((int)compressedSize),
                uncompressedSize,
                dataOffset,
                localOffset));
            cursor = checked(cursor + 46 + (int)variableLength);
        }

        if (cursor != centralEnd)
            throw Error("DOCUMENT_INVALID_ARCHIVE");

        var ordered = result.OrderBy(entry => entry.LocalHeaderOffset).ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            var entry = ordered[index];
            var nextLocalOffset = index + 1 < ordered.Length ? ordered[index + 1].LocalHeaderOffset : checked((int)centralOffset);
            if (entry.LocalHeaderOffset >= nextLocalOffset ||
                (long)entry.DataOffset + entry.CompressedSize > nextLocalOffset)
                throw Error("DOCUMENT_INVALID_ARCHIVE");
        }

        return result;
    }

    private static int FindEndOfCentralDirectory(byte[] archiveBytes, CancellationToken token, long startedAt)
    {
        var earliest = Math.Max(0, archiveBytes.Length - 65_557);
        for (var offset = archiveBytes.Length - 22; offset >= earliest; offset--)
        {
            if ((offset & 0x3ff) == 0)
                CheckBudget(token, startedAt);
            if (ReadUInt32(archiveBytes, offset, archiveBytes.Length) != 0x06054b50)
                continue;

            var commentLength = ReadUInt16(archiveBytes, offset + 20, archiveBytes.Length);
            if ((long)offset + 22 + commentLength == archiveBytes.Length)
                return offset;
        }
        return -1;
    }

    private static string DecodeEntryName(byte[] data, int offset, int length, ushort flags)
    {
        EnsureRange(offset, length, data.Length);
        var nameBytes = data.AsSpan(offset, length);
        if ((flags & 0x0800) != 0)
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(nameBytes);
        if (nameBytes.ContainsAnyExceptInRange((byte)0x00, (byte)0x7f))
            throw Error("DOCUMENT_INVALID_ARCHIVE");
        return Encoding.ASCII.GetString(nameBytes);
    }

    private static void EnsureRange(int offset, int length, int limit)
    {
        if (offset < 0 || length < 0 || offset > limit - length)
            throw Error("DOCUMENT_INVALID_ARCHIVE");
    }

    private static ushort ReadUInt16(byte[] data, int offset, int limit)
    {
        EnsureRange(offset, sizeof(ushort), limit);
        return BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, sizeof(ushort)));
    }

    private static uint ReadUInt32(byte[] data, int offset, int limit)
    {
        EnsureRange(offset, sizeof(uint), limit);
        return BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, sizeof(uint)));
    }

    private static uint UpdateCrc32(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var value in data)
            crc = Crc32Table[(int)((crc ^ value) & 0xff)] ^ (crc >> 8);
        return crc;
    }

    private static uint[] CreateCrc32Table()
    {
        var table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            var value = index;
            for (var bit = 0; bit < 8; bit++)
                value = (value & 1) != 0 ? 0xedb88320 ^ (value >> 1) : value >> 1;
            table[index] = value;
        }
        return table;
    }

    private readonly record struct RawEntry(
        ushort CompressionMethod,
        uint Crc32,
        int CompressedSize,
        long UncompressedSize,
        int DataOffset,
        int LocalHeaderOffset);

    private static string ExtractXml(byte[] documentXml, CancellationToken token, long startedAt)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumDocumentXmlBytes,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            CheckCharacters = true,
            CloseInput = true,
        };

        using var input = new MemoryStream(documentXml, writable: false);
        using var reader = XmlReader.Create(input, settings);
        var output = new StringBuilder();
        var outputBytes = 0;
        var rootSeen = false;
        var inParagraph = false;
        var paragraphDepth = -1;
        var textDepth = -1;
        var paragraphCount = 0;

        while (true)
        {
            CheckBudget(token, startedAt);
            if (!reader.Read())
                break;

            if (reader.NodeType == XmlNodeType.Element)
            {
                if (!rootSeen)
                {
                    rootSeen = true;
                    if (reader.LocalName != "document" || reader.NamespaceURI != WordNamespace)
                        throw Error("DOCUMENT_INVALID_ARCHIVE");
                }

                if (reader.NamespaceURI != WordNamespace)
                    continue;

                if (reader.LocalName == "p")
                {
                    if (inParagraph)
                        throw Error("DOCUMENT_INVALID_ARCHIVE");
                    if (paragraphCount > 0)
                        Append(output, "\n", ref outputBytes);
                    paragraphCount++;
                    paragraphDepth = reader.Depth;
                    inParagraph = !reader.IsEmptyElement;
                    if (reader.IsEmptyElement)
                        paragraphDepth = -1;
                }
                else if (inParagraph && reader.LocalName == "t")
                {
                    textDepth = reader.IsEmptyElement ? -1 : reader.Depth;
                }
                else if (inParagraph && reader.LocalName == "tab")
                {
                    Append(output, "\t", ref outputBytes);
                }
                else if (inParagraph && reader.LocalName is "br" or "cr")
                {
                    Append(output, "\n", ref outputBytes);
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement)
            {
                if (reader.Depth == textDepth && reader.NamespaceURI == WordNamespace && reader.LocalName == "t")
                    textDepth = -1;

                if (inParagraph && reader.Depth == paragraphDepth &&
                    reader.NamespaceURI == WordNamespace && reader.LocalName == "p")
                {
                    inParagraph = false;
                    paragraphDepth = -1;
                }
            }
            else if (inParagraph && textDepth >= 0 &&
                reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace)
            {
                Append(output, reader.Value, ref outputBytes);
            }
        }

        if (!rootSeen)
            throw Error("DOCUMENT_INVALID_ARCHIVE");

        var extracted = output.ToString().Trim('\n');
        if (string.IsNullOrWhiteSpace(extracted))
            throw Error("DOCUMENT_NO_TEXT");
        return extracted;
    }

    private static void Append(StringBuilder output, string value, ref int outputBytes)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);
        if (byteCount > MaximumOutputBytes - outputBytes)
            throw Error("DOCUMENT_OUTPUT_TOO_LARGE");
        output.Append(value);
        outputBytes += byteCount;
    }

    private static void ValidateEntryName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.StartsWith('/') || name.Contains('\\') ||
            name.Contains(':') || name.Any(char.IsControl))
            throw Error("DOCUMENT_INVALID_ARCHIVE");

        var path = name.EndsWith('/') ? name[..^1] : name;
        if (path.Length == 0 || path.Split('/').Any(segment => segment is "" or "." or ".."))
            throw Error("DOCUMENT_INVALID_ARCHIVE");
    }

    private static void CheckBudget(CancellationToken token, long startedAt)
    {
        token.ThrowIfCancellationRequested();
        if (Stopwatch.GetElapsedTime(startedAt) > TimeSpan.FromSeconds(5))
            throw Error("DOCUMENT_TIMEOUT");
    }

    private static DocumentExtractionException Error(string code) => new(code);
}
