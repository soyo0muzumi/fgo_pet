using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using FgoPet.Plugin.Documents;
using Xunit;

namespace FgoPet.Capability.Tests.Documents;

public sealed class DocxTextExtractorTests
{
    private const string WordNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    [Fact]
    public void Extracts_text_with_paragraphs_tabs_and_unicode()
    {
        var xml = Wrap("<w:p><w:r><w:t>你好，Mash 🐈</w:t><w:tab/><w:t>tab 后</w:t></w:r></w:p>" +
            "<w:p><w:r><w:t>第二段</w:t><w:br/><w:t>换行</w:t></w:r></w:p>");

        var text = DocxTextExtractor.Extract(CreateZip(("word/document.xml", Encoding.UTF8.GetBytes(xml))), default);

        Assert.Equal("你好，Mash 🐈\ttab 后\n第二段\n换行", text);
    }

    [Fact]
    public void Rejects_input_larger_than_eight_mib()
    {
        var error = Assert.Throws<DocumentExtractionException>(() =>
            DocxTextExtractor.Extract(new byte[8 * 1024 * 1024 + 1], default));

        Assert.Equal("DOCUMENT_INPUT_TOO_LARGE", error.Code);
        Assert.Equal(error.Code, error.Message);
    }

    [Fact]
    public void Rejects_more_than_256_zip_entries()
    {
        var entries = new List<(string Name, byte[] Content)>
        {
            ("word/document.xml", Encoding.UTF8.GetBytes(Wrap("<w:p><w:r><w:t>ok</w:t></w:r></w:p>"))),
        };
        for (var index = 0; index < 256; index++) entries.Add(($"extra/entry-{index:D3}.bin", []));

        var error = Assert.Throws<DocumentExtractionException>(() =>
            DocxTextExtractor.Extract(CreateZip(entries.ToArray()), default));

        Assert.Equal("DOCUMENT_ARCHIVE_TOO_LARGE", error.Code);
    }

    [Fact]
    public void Rejects_document_xml_over_four_mib()
    {
        var xml = Wrap("<w:p><w:r><w:t>" + new string('x', 4 * 1024 * 1024) + "</w:t></w:r></w:p>");

        var error = Assert.Throws<DocumentExtractionException>(() =>
            DocxTextExtractor.Extract(CreateZip(("word/document.xml", Encoding.UTF8.GetBytes(xml))), default));

        Assert.Equal("DOCUMENT_ARCHIVE_TOO_LARGE", error.Code);
    }

    [Fact]
    public void Rejects_total_declared_uncompressed_size_over_16_mib()
    {
        var zip = CreateZip(
            ("word/document.xml", Encoding.UTF8.GetBytes(Wrap("<w:p><w:r><w:t>ok</w:t></w:r></w:p>"))),
            ("custom/data.bin", new byte[16 * 1024 * 1024 + 1]));

        var error = Assert.Throws<DocumentExtractionException>(() => DocxTextExtractor.Extract(zip, default));

        Assert.Equal("DOCUMENT_ARCHIVE_TOO_LARGE", error.Code);
    }

    [Fact]
    public void Enforces_actual_decompression_limit_when_zip_declares_a_smaller_part()
    {
        var xml = Wrap("<w:p><w:r><w:t>" + new string('x', 4 * 1024 * 1024 + 1) + "</w:t></w:r></w:p>");
        var zip = CreateZip(("word/document.xml", Encoding.UTF8.GetBytes(xml)));
        PatchDeclaredLength(zip, "word/document.xml", 1);

        var error = Assert.Throws<DocumentExtractionException>(() => DocxTextExtractor.Extract(zip, default));

        Assert.Equal("DOCUMENT_ARCHIVE_TOO_LARGE", error.Code);
    }

    [Fact]
    public void Rejects_extracted_utf8_text_over_one_mib()
    {
        var xml = Wrap("<w:p><w:r><w:t>" + new string('x', 1024 * 1024 + 1) + "</w:t></w:r></w:p>");

        var error = Assert.Throws<DocumentExtractionException>(() =>
            DocxTextExtractor.Extract(CreateZip(("word/document.xml", Encoding.UTF8.GetBytes(xml))), default));

        Assert.Equal("DOCUMENT_OUTPUT_TOO_LARGE", error.Code);
    }

    [Fact]
    public void Prohibits_dtd_and_entity_expansion()
    {
        var xml = "<?xml version=\"1.0\"?><!DOCTYPE w:document [<!ENTITY payload \"hidden\">]>" +
            $"<w:document xmlns:w=\"{WordNamespace}\"><w:body><w:p><w:r><w:t>&payload;</w:t></w:r></w:p></w:body></w:document>";

        var error = Assert.Throws<DocumentExtractionException>(() =>
            DocxTextExtractor.Extract(CreateZip(("word/document.xml", Encoding.UTF8.GetBytes(xml))), default));

        Assert.Equal("DOCUMENT_INVALID_ARCHIVE", error.Code);
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("/rooted.txt")]
    [InlineData("folder/../outside.txt")]
    [InlineData("folder\\outside.txt")]
    [InlineData("C:/outside.txt")]
    public void Rejects_unsafe_archive_entry_names(string unsafeName)
    {
        var zip = CreateZip(
            ("word/document.xml", Encoding.UTF8.GetBytes(Wrap("<w:p><w:r><w:t>ok</w:t></w:r></w:p>"))),
            (unsafeName, Encoding.UTF8.GetBytes("fixture")));

        var error = Assert.Throws<DocumentExtractionException>(() => DocxTextExtractor.Extract(zip, default));

        Assert.Equal("DOCUMENT_INVALID_ARCHIVE", error.Code);
    }

    [Fact]
    public void Rejects_duplicate_document_parts()
    {
        var content = Encoding.UTF8.GetBytes(Wrap("<w:p><w:r><w:t>duplicate</w:t></w:r></w:p>"));
        var error = Assert.Throws<DocumentExtractionException>(() => DocxTextExtractor.Extract(
            CreateZip(("word/document.xml", content), ("word/document.xml", content)), default));

        Assert.Equal("DOCUMENT_INVALID_ARCHIVE", error.Code);
    }

    [Fact]
    public void Rejects_missing_document_xml_part()
    {
        var error = Assert.Throws<DocumentExtractionException>(() => DocxTextExtractor.Extract(
            CreateZip(("word/comments.xml", Encoding.UTF8.GetBytes(Wrap("<w:p/>"))),
                ("[Content_Types].xml", Encoding.UTF8.GetBytes("<Types/>"))), default));

        Assert.Equal("DOCUMENT_INVALID_ARCHIVE", error.Code);
    }

    [Fact]
    public void Reports_no_text_for_empty_or_whitespace_only_document()
    {
        var xml = Wrap("<w:p><w:r><w:t>   </w:t></w:r></w:p><w:p/>");

        var error = Assert.Throws<DocumentExtractionException>(() =>
            DocxTextExtractor.Extract(CreateZip(("word/document.xml", Encoding.UTF8.GetBytes(xml))), default));

        Assert.Equal("DOCUMENT_NO_TEXT", error.Code);
    }

    [Fact]
    public void Rejects_damaged_zip_with_a_safe_error()
    {
        var error = Assert.Throws<DocumentExtractionException>(() =>
            DocxTextExtractor.Extract(Encoding.UTF8.GetBytes("not a zip archive"), default));

        Assert.Equal("DOCUMENT_INVALID_ARCHIVE", error.Code);
        Assert.Equal(error.Code, error.Message);
    }

    [Fact]
    public void Honors_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => DocxTextExtractor.Extract(
            CreateZip(("word/document.xml", Encoding.UTF8.GetBytes(Wrap("<w:p><w:r><w:t>ok</w:t></w:r></w:p>")))),
            cancellation.Token));
    }

    private static string Wrap(string body) =>
        $"<?xml version=\"1.0\" encoding=\"utf-8\"?><w:document xmlns:w=\"{WordNamespace}\"><w:body>{body}</w:body></w:document>";

    private static byte[] CreateZip(params (string Name, byte[] Content)[] entries)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                using var stream = entry.Open();
                stream.Write(content);
            }
        }
        return output.ToArray();
    }

    private static void PatchDeclaredLength(byte[] zip, string entryName, uint length)
    {
        for (var offset = 0; offset <= zip.Length - 46; offset++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(offset, 4)) != 0x02014b50) continue;
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(offset + 28, 2));
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(offset + 30, 2));
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(offset + 32, 2));
            if (offset + 46 + nameLength + extraLength + commentLength > zip.Length) break;
            var name = Encoding.UTF8.GetString(zip, offset + 46, nameLength);
            if (!string.Equals(name, entryName, StringComparison.Ordinal))
            {
                offset += 45 + nameLength + extraLength + commentLength;
                continue;
            }

            BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(offset + 24, 4), length);
            var localOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(offset + 42, 4)));
            if (BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(localOffset, 4)) != 0x04034b50)
                throw new InvalidOperationException("Fixture local header is missing.");
            BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(localOffset + 22, 4), length);
            return;
        }

        throw new InvalidOperationException("Fixture central directory entry is missing.");
    }
}
