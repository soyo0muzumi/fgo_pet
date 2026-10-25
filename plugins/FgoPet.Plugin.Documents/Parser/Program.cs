using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

// Trusted executable, untrusted stdin bytes. The host controls Job limits and performs source/path authorization.
Console.OutputEncoding = new UTF8Encoding(false);
var json = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
try
{
    if (args.Length != 1 || !int.TryParse(args[0], out var page) || page is < 1 or > 256)
        throw new InvalidDataException();
    using var input = Console.OpenStandardInput();
    using var retained = new MemoryStream();
    var buffer = new byte[8192];
    while (true)
    {
        var read = input.Read(buffer);
        if (read == 0) break;
        if (retained.Length + read > 8388608) { Fail("DOCUMENT_INPUT_TOO_LARGE"); return; }
        retained.Write(buffer, 0, read);
    }
    var bytes = retained.ToArray();
    if (bytes.Length < 5 || !bytes.AsSpan(0, 5).SequenceEqual("%PDF-"u8)) { Fail("DOCUMENT_INVALID"); return; }
    using var document = PdfDocument.Open(bytes, new ParsingOptions { UseLenientParsing = false });
    if (document.IsEncrypted) { Fail("DOCUMENT_ENCRYPTED"); return; }
    if (document.NumberOfPages is < 1 or > 256) { Fail("DOCUMENT_PAGE_LIMIT"); return; }
    if (page > document.NumberOfPages) { Fail("DOCUMENT_PAGE_NOT_FOUND"); return; }
    var text = ContentOrderTextExtractor.GetText(document.GetPage(page));
    if (string.IsNullOrWhiteSpace(text)) { Fail("DOCUMENT_NO_TEXT"); return; }
    if (Encoding.UTF8.GetByteCount(text) > 1048576) { Fail("DOCUMENT_OUTPUT_TOO_LARGE"); return; }
    var result = JsonSerializer.Serialize(new { success = true, page, totalPages = document.NumberOfPages,
        content = text, sourceVersion = Convert.ToHexString(SHA256.HashData(bytes)) }, json);
    if (Encoding.UTF8.GetByteCount(result) > 2097152) { Fail("DOCUMENT_OUTPUT_TOO_LARGE"); return; }
    Console.Write(result);
}
catch (Exception error)
{
    // Only a stable public error code crosses the process boundary.
    Fail(error.GetType().Name.Contains("Encrypt", StringComparison.OrdinalIgnoreCase) ? "DOCUMENT_ENCRYPTED" : "DOCUMENT_INVALID");
}

void Fail(string code)
{
    Console.Write(JsonSerializer.Serialize(new { success = false, errorCode = code }, json));
    Environment.ExitCode = 2;
}
