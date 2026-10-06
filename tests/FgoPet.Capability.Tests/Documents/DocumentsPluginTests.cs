using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FgoPet.Extensibility;
using FgoPet.Platform.Processes;
using FgoPet.Platform.Windows.Processes;
using FgoPet.Plugin.Documents;
using FgoPet.Plugin.Workspace;
using Xunit;

namespace FgoPet.Capability.Tests.Documents;

public sealed class DocumentsPluginTests
{
    [Fact]
    public async Task Real_pdf_helper_supports_pages_cursors_and_source_change_detection()
    {
        using var workspace = new WorkspaceFixture();
        var pdfPath = workspace.Write("pages.pdf", CreatePdf("first-page", "second-page"));
        var helper = ParserPath;
        Assert.True(File.Exists(helper));
        Assert.Contains("Apache License", File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "licenses", "PdfPig-LICENSE.txt")));

        var tool = CreateTool(workspace, new WindowsBoundedProcessRunner(), helper);
        var first = await Invoke(tool, workspace, new { path = "pages.pdf", limit = 5 });
        Assert.True(first.Success, first.ErrorCode);
        Assert.Equal("first", first.Payload.GetProperty("content").GetString());
        Assert.Equal(1, first.Payload.GetProperty("page").GetInt32());
        Assert.Equal(2, first.Payload.GetProperty("totalPages").GetInt32());
        Assert.True(first.Payload.GetProperty("truncated").GetBoolean());
        var withinPageCursor = first.Payload.GetProperty("nextCursor").GetString();
        Assert.NotNull(withinPageCursor);

        var remainder = await Invoke(tool, workspace, new { path = "pages.pdf", cursor = withinPageCursor, limit = 10 });
        Assert.True(remainder.Success, remainder.ErrorCode);
        Assert.Equal("-page", remainder.Payload.GetProperty("content").GetString());
        Assert.Equal(1, remainder.Payload.GetProperty("page").GetInt32());
        var nextPageCursor = remainder.Payload.GetProperty("nextCursor").GetString();
        Assert.NotNull(nextPageCursor);

        var second = await Invoke(tool, workspace, new { path = "pages.pdf", cursor = nextPageCursor, limit = 100 });
        Assert.True(second.Success, second.ErrorCode);
        Assert.Equal("second-page", second.Payload.GetProperty("content").GetString());
        Assert.Equal(2, second.Payload.GetProperty("page").GetInt32());
        Assert.False(second.Payload.GetProperty("truncated").GetBoolean());
        Assert.Equal(JsonValueKind.Null, second.Payload.GetProperty("nextCursor").ValueKind);

        File.WriteAllBytes(pdfPath, CreatePdf("changed-page-one", "changed-page-two"));
        var stale = await Invoke(tool, workspace, new { path = "pages.pdf", cursor = nextPageCursor, limit = 100 });
        Assert.False(stale.Success);
        Assert.Equal("SOURCE_CHANGED", stale.ErrorCode);
    }

    [Fact]
    public async Task Real_pdf_helper_reports_empty_corrupt_and_out_of_range_documents_safely()
    {
        using var workspace = new WorkspaceFixture();
        workspace.Write("empty.pdf", CreatePdf(string.Empty));
        workspace.Write("corrupt.pdf", Encoding.ASCII.GetBytes("%PDF-1.4\nnot a PDF body"));
        workspace.Write("single.pdf", CreatePdf("one page"));
        var tool = CreateTool(workspace, new WindowsBoundedProcessRunner(), ParserPath);

        Assert.Equal("DOCUMENT_NO_TEXT", (await Invoke(tool, workspace, new { path = "empty.pdf" })).ErrorCode);
        Assert.Equal("DOCUMENT_INVALID", (await Invoke(tool, workspace, new { path = "corrupt.pdf" })).ErrorCode);
        Assert.Equal("DOCUMENT_PAGE_NOT_FOUND", (await Invoke(tool, workspace, new { path = "single.pdf", page = 2 })).ErrorCode);
    }

    [Fact]
    public async Task Text_reader_handles_utf8_and_utf16_boms_chunks_and_cursor_binding()
    {
        using var workspace = new WorkspaceFixture();
        var utf8 = new byte[] { 0xef, 0xbb, 0xbf }.Concat(Encoding.UTF8.GetBytes("你好🌟尾")).ToArray();
        var utf16 = new byte[] { 0xff, 0xfe }.Concat(new UnicodeEncoding(false, false).GetBytes("猫🐈tail")).ToArray();
        workspace.Write("notes.md", utf8);
        workspace.Write("utf16.txt", utf16);
        var tool = CreateTool(workspace, new RecordingRunner(), ParserPath);

        var first = await Invoke(tool, workspace, new { path = "notes.md", limit = 2 });
        Assert.True(first.Success, first.ErrorCode);
        Assert.Equal("你好", first.Payload.GetProperty("content").GetString());
        Assert.Equal(VersionOf(utf8), first.Payload.GetProperty("sourceVersion").GetString());
        var cursor = first.Payload.GetProperty("nextCursor").GetString();
        Assert.NotNull(cursor);
        var second = await Invoke(tool, workspace, new { path = "notes.md", cursor, limit = 2 });
        Assert.Equal("🌟尾", second.Payload.GetProperty("content").GetString());
        Assert.Equal(VersionOf(utf8), second.Payload.GetProperty("sourceVersion").GetString());

        var utf16First = await Invoke(tool, workspace, new { path = "utf16.txt", limit = 3 });
        Assert.True(utf16First.Success, utf16First.ErrorCode);
        Assert.Equal("猫🐈t", utf16First.Payload.GetProperty("content").GetString());
        Assert.Equal(VersionOf(utf16), utf16First.Payload.GetProperty("sourceVersion").GetString());
        var utf16Cursor = utf16First.Payload.GetProperty("nextCursor").GetString();
        Assert.Equal("ail", (await Invoke(tool, workspace, new { path = "utf16.txt", cursor = utf16Cursor, limit = 5 }))
            .Payload.GetProperty("content").GetString());

        Assert.Equal("DOCUMENT_INVALID_CURSOR",
            (await Invoke(tool, workspace, new { path = "notes.md", page = 1, cursor = new string('A', 64) + ".2.0" })).ErrorCode);
        Assert.Equal("DOCUMENT_PAGE_NOT_FOUND", (await Invoke(tool, workspace, new { path = "notes.md", page = 2 })).ErrorCode);
    }

    [Fact]
    public async Task Synthetic_docx_uses_workspace_guard_and_does_not_launch_pdf_helper()
    {
        using var workspace = new WorkspaceFixture();
        workspace.Write("note.docx", CreateDocx(
            "<w:p><w:r><w:t>文档 🐈</w:t><w:tab/><w:t>尾部</w:t></w:r></w:p>" +
            "<w:p><w:r><w:t>下一段</w:t></w:r></w:p>"));
        var runner = new RecordingRunner();
        var tool = CreateTool(workspace, runner, ParserPath);

        var result = await Invoke(tool, workspace, new { path = "note.docx" });

        Assert.True(result.Success, result.ErrorCode);
        Assert.Equal("文档 🐈\t尾部\n下一段", result.Payload.GetProperty("content").GetString());
        Assert.Equal("application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            result.Payload.GetProperty("mimeType").GetString());
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public async Task Missing_pdf_helper_fails_before_process_launch()
    {
        using var workspace = new WorkspaceFixture();
        workspace.Write("note.pdf", CreatePdf("safe fixture"));
        var runner = new RecordingRunner();
        var missing = Path.Combine(workspace.Root, "missing-parser.exe");
        var tool = CreateTool(workspace, runner, missing);

        var result = await Invoke(tool, workspace, new { path = "note.pdf" });

        Assert.Equal("DOCUMENT_PARSER_UNAVAILABLE", result.ErrorCode);
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public async Task Pdf_runner_timeout_and_malformed_result_fail_safely()
    {
        using var workspace = new WorkspaceFixture();
        workspace.Write("note.pdf", CreatePdf("safe fixture"));

        var timeoutRunner = new RecordingRunner(_ => new(false, BoundedProcessExit.TimedOut, null, "", "", false));
        var timeoutTool = CreateTool(workspace, timeoutRunner, ParserPath);
        Assert.Equal("DOCUMENT_PARSE_TIMEOUT", (await Invoke(timeoutTool, workspace, new { path = "note.pdf" })).ErrorCode);
        Assert.Equal(TimeSpan.FromSeconds(5), timeoutRunner.LastRequest!.Timeout);
        Assert.Equal(2 * 1024 * 1024, timeoutRunner.LastRequest.MaxOutputBytes);
        Assert.Equal(1, timeoutRunner.LastRequest.MaxProcesses);

        var malformedRunner = new RecordingRunner(_ => new(true, BoundedProcessExit.Completed, 0, "not-json", "", false));
        var malformedTool = CreateTool(workspace, malformedRunner, ParserPath);
        Assert.Equal("DOCUMENT_READ_FAILED",
            (await Invoke(malformedTool, workspace, new { path = "note.pdf" })).ErrorCode);
        Assert.NotNull(malformedRunner.LastRequest!.BeforeStart);
    }

    [Fact]
    public async Task Revoked_workspace_authority_blocks_a_completed_pdf_result()
    {
        using var workspace = new WorkspaceFixture();
        var pdf = CreatePdf("safe fixture");
        workspace.Write("note.pdf", pdf);
        var runner = new RecordingRunner(request =>
        {
            workspace.Current = workspace.Current with
            {
                Authorization = workspace.Current.Authorization with { Revision = workspace.Current.Authorization.Revision + 1 },
            };
            var response = JsonSerializer.Serialize(new
            {
                success = true,
                page = 1,
                totalPages = 1,
                content = "safe fixture",
                sourceVersion = VersionOf(request.StandardInput.ToArray()),
            });
            return new(true, BoundedProcessExit.Completed, 0, response, "", false);
        });
        var tool = CreateTool(workspace, runner, ParserPath);

        var result = await Invoke(tool, workspace, new { path = "note.pdf" });

        Assert.Equal("WORKSPACE_SCOPE_CHANGED", result.ErrorCode);
        Assert.Equal(1, runner.Calls);
    }

    private static string ParserPath => Path.Combine(AppContext.BaseDirectory, "FgoPet.DocumentParser.exe");

    private static DocumentsPlugin CreateTool(WorkspaceFixture workspace, IBoundedProcessRunner runner, string helper) =>
        new(workspace.Guard, runner, helper);

    private static ValueTask<ToolResult> Invoke(DocumentsPlugin plugin, WorkspaceFixture workspace, object arguments)
    {
        var tool = plugin.Contributions.Tools.Single();
        var authorization = ((IToolResourceAuthorizationProvider)tool).GetAuthorization(workspace.Scope);
        return tool.InvokeAsync(new ToolInvocation(workspace.Scope, JsonSerializer.SerializeToElement(arguments))
        {
            ExecutionContext = new ToolExecutionContext("test-run", 1, "test-call", new string('A', 64))
            {
                ResourceAuthorization = authorization,
            },
        }, CancellationToken.None);
    }

    private static byte[] CreatePdf(params string[] pageTexts)
    {
        var pageCount = pageTexts.Length;
        var fontObject = 3 + (2 * pageCount);
        var objectCount = fontObject;
        var offsets = new long[objectCount + 1];
        using var output = new MemoryStream();
        void Write(string text) => output.Write(Encoding.ASCII.GetBytes(text));
        void WriteObject(int id, string body)
        {
            offsets[id] = output.Position;
            Write($"{id} 0 obj\n{body}\nendobj\n");
        }

        Write("%PDF-1.4\n");
        WriteObject(1, "<< /Type /Catalog /Pages 2 0 R >>");
        var kids = string.Join(" ", Enumerable.Range(0, pageCount).Select(index => $"{3 + 2 * index} 0 R"));
        WriteObject(2, $"<< /Type /Pages /Kids [{kids}] /Count {pageCount} >>");
        for (var index = 0; index < pageCount; index++)
        {
            var pageObject = 3 + (2 * index);
            var contentObject = pageObject + 1;
            WriteObject(pageObject, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                $"/Resources << /Font << /F1 {fontObject} 0 R >> >> /Contents {contentObject} 0 R >>");
            var content = string.IsNullOrEmpty(pageTexts[index])
                ? "q Q\n"
                : $"BT /F1 12 Tf 72 720 Td ({EscapePdfLiteral(pageTexts[index])}) Tj ET\n";
            var contentBytes = Encoding.ASCII.GetBytes(content);
            offsets[contentObject] = output.Position;
            Write($"{contentObject} 0 obj\n<< /Length {contentBytes.Length} >>\nstream\n");
            output.Write(contentBytes);
            Write("endstream\nendobj\n");
        }
        WriteObject(fontObject, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");

        var xrefOffset = output.Position;
        Write($"xref\n0 {objectCount + 1}\n0000000000 65535 f \n");
        for (var id = 1; id <= objectCount; id++)
            Write($"{offsets[id]:D10} 00000 n \n");
        Write($"trailer\n<< /Size {objectCount + 1} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF\n");
        return output.ToArray();
    }

    private static string EscapePdfLiteral(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("(", "\\(", StringComparison.Ordinal)
            .Replace(")", "\\)", StringComparison.Ordinal);

    private static byte[] CreateDocx(string body)
    {
        const string wordNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var xml = $"<?xml version=\"1.0\" encoding=\"utf-8\"?><w:document xmlns:w=\"{wordNamespace}\"><w:body>{body}</w:body></w:document>";
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var stream = archive.CreateEntry("word/document.xml", CompressionLevel.Optimal).Open();
            var bytes = Encoding.UTF8.GetBytes(xml);
            stream.Write(bytes);
        }
        return output.ToArray();
    }

    private static string VersionOf(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private sealed class RecordingRunner(Func<BoundedProcessRequest, BoundedProcessResult>? response = null) : IBoundedProcessRunner
    {
        public int Calls { get; private set; }
        public BoundedProcessRequest? LastRequest { get; private set; }

        public ValueTask<BoundedProcessResult> RunAsync(BoundedProcessRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Calls++;
            LastRequest = request;
            request.BeforeStart?.Invoke();
            return ValueTask.FromResult(response?.Invoke(request) ??
                new BoundedProcessResult(true, BoundedProcessExit.Completed, 0, "{}", "", false));
        }
    }

    private sealed class WorkspaceFixture : IWorkspaceAuthorizationSource, IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "fgopet-documents-" + Guid.NewGuid().ToString("N"));
        public ToolScope Scope { get; } = new("conversation", "role", "project");
        public WorkspaceAuthorization Current { get; set; }
        public WorkspaceAccessGuard Guard { get; }

        public WorkspaceFixture()
        {
            Directory.CreateDirectory(Root);
            Current = new(Scope, new("fixture-root", 1), Root, Root);
            Guard = new WorkspaceAccessGuard(this);
        }

        public string Write(string name, byte[] content)
        {
            var path = Path.Combine(Root, name);
            File.WriteAllBytes(path, content);
            return path;
        }

        public WorkspaceAuthorization? GetCurrent(ToolScope scope) => scope == Scope ? Current : null;

        public void Dispose()
        {
            if (!Directory.Exists(Root))
                return;
            foreach (var file in Directory.GetFiles(Root))
                File.Delete(file);
            Directory.Delete(Root);
        }
    }
}
