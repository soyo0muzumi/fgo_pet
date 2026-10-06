using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using FgoPet.Extensibility;
using FgoPet.Platform.Processes;

namespace FgoPet.Plugin.Documents;

public sealed class DocumentsPlugin : IFgoPetPlugin, IDisposable
{
    public PluginManifest Manifest { get; } = new("documents", "1.0.0", 1, []);
    public PluginContributions Contributions { get; }
    public DocumentsPlugin(IWorkspaceAccessGuard guard, IBoundedProcessRunner runner, string parserExecutable) =>
        Contributions = PluginContributions.Empty with { Tools = [new Reader(guard, runner, Path.GetFullPath(parserExecutable))] };
    public ValueTask StartAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }
    public ValueTask StopAsync(CancellationToken token) => ValueTask.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public void Dispose() { }

    private sealed class Reader(IWorkspaceAccessGuard guard, IBoundedProcessRunner runner, string parser) : IToolProvider, IToolResourceAuthorizationProvider
    {
        private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        public ToolDescriptor Descriptor { get; } = new("document.read", "Extract a bounded, versioned text chunk from PDF, DOCX, Markdown or TXT. No OCR or embedded code execution.", """
            {"type":"object","properties":{"path":{"type":"string","minLength":1,"maxLength":1024},
            "page":{"type":"integer","minimum":1,"maximum":256},"cursor":{"type":"string","maxLength":96},
            "limit":{"type":"integer","minimum":1,"maximum":8000}},"required":["path"],"additionalProperties":false}
            """, ToolEffect.ReadOnly);
        public ToolResourceAuthorization GetAuthorization(ToolScope scope) => guard.GetAuthorization(scope).Authorization;
        public async ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken token)
        {
            try
            {
                token.ThrowIfCancellationRequested();
                var args = invocation.Arguments;
                var path = args.GetProperty("path").GetString()!;
                var page = args.TryGetProperty("page", out var pageValue) ? pageValue.GetInt32() : 1;
                var limit = args.TryGetProperty("limit", out var limitValue) ? limitValue.GetInt32() : 4000;
                var offset = 0;
                string? expected = null;
                if (args.TryGetProperty("cursor", out var cursorValue))
                {
                    var parts = cursorValue.GetString()!.Split('.');
                    if (parts.Length != 3 || parts[0].Length != 64 || !parts[0].All(c => c is >= 'A' and <= 'F' or >= '0' and <= '9') ||
                        !int.TryParse(parts[1], out var cursorPage) || !int.TryParse(parts[2], out offset) ||
                        cursorPage is < 1 or > 256 || offset is < 0 or > 1048576 || args.TryGetProperty("page", out _) && cursorPage != page)
                        return Failure("DOCUMENT_INVALID_CURSOR");
                    expected = parts[0]; page = cursorPage;
                }
                if (page is < 1 or > 256 || limit is < 1 or > 8000) return Failure("TOOL_INVALID_ARGUMENTS");
                using var lease = guard.Open(invocation.Scope, path, WorkspacePathKind.File);
                if (invocation.ExecutionContext?.ResourceAuthorization is not null && invocation.ExecutionContext.ResourceAuthorization != lease.Authority.Authorization)
                    return Failure("WORKSPACE_SCOPE_CHANGED");
                var bytes = await Read(lease, token);
                var version = Convert.ToHexString(SHA256.HashData(bytes));
                if (expected is not null && expected != version) return Failure("SOURCE_CHANGED");
                var extension = Path.GetExtension(lease.FullPath).ToLowerInvariant();
                var totalPages = 1;
                string text, mime;
                switch (extension)
                {
                    case ".pdf":
                        mime = "application/pdf";
                        if (!File.Exists(parser)) return Failure("DOCUMENT_PARSER_UNAVAILABLE");
                        var process = await runner.RunAsync(new BoundedProcessRequest(parser, [page.ToString(System.Globalization.CultureInfo.InvariantCulture)],
                            lease.Authority.CurrentDirectory, TimeSpan.FromSeconds(5), MaxOutputBytes: 2097152, MaxProcesses: 1) {
                                StandardInput = bytes, BeforeStart = lease.Revalidate,
                                Correlation = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                                    invocation.ExecutionContext?.IdempotencyKey ?? version)))[..24] }, token);
                        token.ThrowIfCancellationRequested();
                        if (!process.Started || process.Exit != BoundedProcessExit.Completed || process.Truncated)
                            return Failure(process.Exit == BoundedProcessExit.TimedOut ? "DOCUMENT_PARSE_TIMEOUT" : "DOCUMENT_PARSE_FAILED");
                        using (var result = JsonDocument.Parse(process.StandardOutput, new() { MaxDepth = 8 }))
                        {
                            var body = result.RootElement;
                            if (!body.GetProperty("success").GetBoolean())
                            {
                                var code = body.GetProperty("errorCode").GetString();
                                return Failure(code is "DOCUMENT_INPUT_TOO_LARGE" or "DOCUMENT_INVALID" or "DOCUMENT_PAGE_LIMIT" or
                                    "DOCUMENT_PAGE_NOT_FOUND" or "DOCUMENT_NO_TEXT" or "DOCUMENT_OUTPUT_TOO_LARGE" or "DOCUMENT_ENCRYPTED" ? code : "DOCUMENT_PARSE_FAILED");
                            }
                            if (process.ExitCode != 0 || body.GetProperty("sourceVersion").GetString() != version || body.GetProperty("page").GetInt32() != page)
                                return Failure("DOCUMENT_PARSE_FAILED");
                            totalPages = body.GetProperty("totalPages").GetInt32();
                            if (totalPages is < 1 or > 256 || page > totalPages) return Failure("DOCUMENT_PARSE_FAILED");
                            text = body.GetProperty("content").GetString()!;
                        }
                        break;
                    case ".docx":
                        if (page != 1) return Failure("DOCUMENT_PAGE_NOT_FOUND");
                        mime = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
                        text = DocxTextExtractor.Extract(bytes, token);
                        break;
                    case ".md": case ".txt":
                        if (page != 1) return Failure("DOCUMENT_PAGE_NOT_FOUND");
                        mime = extension == ".md" ? "text/markdown" : "text/plain";
                        text = Text(bytes);
                        break;
                    default: return Failure("DOCUMENT_UNSUPPORTED");
                }
                if (string.IsNullOrWhiteSpace(text)) return Failure("DOCUMENT_NO_TEXT");
                if (Encoding.UTF8.GetByteCount(text) > 8388608) return Failure("DOCUMENT_OUTPUT_TOO_LARGE");
                var runes = text.EnumerateRunes().ToArray();
                if (offset > runes.Length) return Failure("DOCUMENT_INVALID_CURSOR");
                var count = Math.Min(limit, runes.Length - offset);
                var output = new StringBuilder();
                for (var index = offset; index < offset + count; index++) output.Append(runes[index].ToString());
                var moreInPage = offset + count < runes.Length;
                var truncated = moreInPage || page < totalPages;
                var next = moreInPage ? version + "." + page + "." + (offset + count) : page < totalPages ? version + "." + (page + 1) + ".0" : null;
                lease.Revalidate();
                return new(true, JsonSerializer.SerializeToElement(new { title = Path.GetFileName(lease.FullPath), mimeType = mime,
                    page, totalPages, chunk = offset, content = output.ToString(), sourceVersion = version, truncated, nextCursor = next }, Json));
            }
            catch (OperationCanceledException) { throw; }
            catch (WorkspaceAccessException error) { return Failure(error.Code); }
            catch (DocumentExtractionException error) { return Failure(error.Code); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or
                ArgumentException or JsonException or KeyNotFoundException) { return Failure("DOCUMENT_READ_FAILED"); }
        }
        private static async Task<byte[]> Read(IWorkspacePathLease lease, CancellationToken token)
        {
            await using var input = new FileStream(lease.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous);
            lease.ValidateOpenedFile(input);
            if (input.Length > 8388608) throw new DocumentExtractionException("DOCUMENT_INPUT_TOO_LARGE");
            var bytes = new byte[(int)input.Length];
            await input.ReadExactlyAsync(bytes, token);
            if (input.ReadByte() != -1) throw new WorkspaceAccessException("SOURCE_CHANGED");
            lease.Revalidate();
            return bytes;
        }
        private static string Text(byte[] bytes)
        {
            Encoding encoding = new UTF8Encoding(false, true);
            var start = 0;
            if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE, 0, 0 }) || bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xFE, 0xFF }))
                throw new DocumentExtractionException("DOCUMENT_UNSUPPORTED_ENCODING");
            if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) start = 3;
            else if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) { encoding = new UnicodeEncoding(false, false, true); start = 2; }
            else if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) { encoding = new UnicodeEncoding(true, false, true); start = 2; }
            string text;
            try { text = encoding.GetString(bytes, start, bytes.Length - start); }
            catch (DecoderFallbackException) { throw new DocumentExtractionException("DOCUMENT_UNSUPPORTED_ENCODING"); }
            if (text.EnumerateRunes().Any(r => Rune.IsControl(r) && r.Value is not ('\n' or '\r' or '\t')))
                throw new DocumentExtractionException("DOCUMENT_UNSUPPORTED_ENCODING");
            return text;
        }
        private static ToolResult Failure(string code) => new(false, JsonSerializer.SerializeToElement(new { }), code)
            { ExecutionState = ToolExecutionState.NotExecuted };
    }
}
