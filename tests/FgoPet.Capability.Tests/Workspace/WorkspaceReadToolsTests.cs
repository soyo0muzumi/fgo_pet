using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FgoPet.Extensibility;
using FgoPet.Plugin.Workspace;
using Xunit;

namespace FgoPet.Capability.Tests.Workspace;

public sealed class WorkspaceReadToolsTests
{
    [Fact]
    public void Factory_exposes_four_read_only_tools_bound_to_host_workspace_authority()
    {
        using var fixture = new Fixture();

        var tools = WorkspaceReadTools.Create(fixture.Guard);

        Assert.Equal(new[] { "workspace.read", "workspace.list", "workspace.glob", "workspace.grep" },
            tools.Select(tool => tool.Descriptor.Name));
        Assert.All(tools, tool =>
        {
            Assert.Equal(ToolEffect.ReadOnly, tool.Descriptor.Effect);
            var authorization = Assert.IsAssignableFrom<IToolResourceAuthorizationProvider>(tool)
                .GetAuthorization(fixture.Scope);
            Assert.Equal(fixture.Guard.GetAuthorization(fixture.Scope).Authorization, authorization);
            Assert.Matches("\\A[0-9A-F]{64}\\z", authorization.Fingerprint);
        });
    }

    [Fact]
    public async Task Read_chunks_by_unicode_scalar_and_rejects_a_changed_source_version()
    {
        using var fixture = new Fixture();
        var bytes = new UTF8Encoding(false, true).GetBytes("alpha😀猫z");
        File.WriteAllBytes(Path.Combine(fixture.Root, "unicode.txt"), bytes);
        var tool = Tool(fixture, "workspace.read");

        var first = await Invoke(tool, fixture.Scope, new { path = "unicode.txt", limit = 6 });
        Assert.True(first.Success);
        Assert.Equal("alpha😀", first.Payload.GetProperty("content").GetString());
        Assert.Equal("utf-8", first.Payload.GetProperty("encoding").GetString());
        Assert.True(first.Payload.GetProperty("truncated").GetBoolean());
        Assert.Equal(6, first.Payload.GetProperty("nextOffset").GetInt64());
        var version = first.Payload.GetProperty("fileVersion").GetString()!;
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), version);

        var second = await Invoke(tool, fixture.Scope,
            new { path = "unicode.txt", offset = 6, limit = 3, fileVersion = version });
        Assert.True(second.Success);
        Assert.Equal("猫z", second.Payload.GetProperty("content").GetString());
        Assert.False(second.Payload.GetProperty("truncated").GetBoolean());
        Assert.False(second.Payload.TryGetProperty("nextOffset", out _));

        File.WriteAllText(Path.Combine(fixture.Root, "unicode.txt"), "changed", new UTF8Encoding(false));
        var changed = await Invoke(tool, fixture.Scope,
            new { path = "unicode.txt", offset = 6, fileVersion = version });
        Assert.False(changed.Success);
        Assert.Equal("SOURCE_CHANGED", changed.ErrorCode);
        Assert.Equal(ToolExecutionState.NotExecuted, changed.ExecutionState);
    }

    [Fact]
    public async Task Read_requires_a_version_for_nonzero_offset_and_rejects_unsupported_text()
    {
        using var fixture = new Fixture();
        var tool = Tool(fixture, "workspace.read");
        File.WriteAllText(Path.Combine(fixture.Root, "text.txt"), "plain", new UTF8Encoding(false));
        var missingVersion = await Invoke(tool, fixture.Scope, new { path = "text.txt", offset = 1 });
        Assert.False(missingVersion.Success);
        Assert.Equal("TOOL_INVALID_ARGUMENTS", missingVersion.ErrorCode);
        Assert.Equal(ToolExecutionState.NotExecuted, missingVersion.ExecutionState);

        File.WriteAllBytes(Path.Combine(fixture.Root, "binary.bin"), [0xC3, 0x28]);
        var binary = await Invoke(tool, fixture.Scope, new { path = "binary.bin" });
        Assert.False(binary.Success);
        Assert.Equal("WORKSPACE_BINARY_OR_UNSUPPORTED_ENCODING", binary.ErrorCode);
        Assert.Equal(ToolExecutionState.NotExecuted, binary.ExecutionState);
    }

    [Fact]
    public async Task Read_revalidates_scope_after_access_before_returning_content()
    {
        using var fixture = new Fixture();
        File.WriteAllText(Path.Combine(fixture.Root, "read.txt"), "must not escape the revoked scope", new UTF8Encoding(false));
        var guard = new RevokingGuard(fixture);
        var tool = Assert.Single(WorkspaceReadTools.Create(guard), provider => provider.Descriptor.Name == "workspace.read");

        var result = await Invoke(tool, fixture.Scope, new { path = "read.txt" });

        Assert.False(result.Success);
        Assert.Equal("WORKSPACE_SCOPE_CHANGED", result.ErrorCode);
        Assert.Equal(ToolExecutionState.NotExecuted, result.ExecutionState);
        Assert.Equal("{}", result.Payload.GetRawText());
    }

    [Fact]
    public void Text_codec_preserves_utf16_bom_and_rejects_invalid_utf8()
    {
        var originalBytes = new byte[] { 0xFF, 0xFE, 0x2B, 0x73 };

        var text = WorkspaceTextCodec.Decode(originalBytes);

        Assert.Equal("猫", text.Content);
        Assert.Equal("utf-16le", text.Encoding);
        Assert.Equal(new byte[] { 0xFF, 0xFE }, text.Preamble);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(originalBytes)), text.Version);
        Assert.Equal(new byte[] { 0xFF, 0xFE, 0xAC, 0x72 }, WorkspaceTextCodec.Encode(text, "犬"));

        var bigEndian = WorkspaceTextCodec.Decode([0xFE, 0xFF, 0x73, 0x2B]);
        Assert.Equal("猫", bigEndian.Content);
        Assert.Equal("utf-16be", bigEndian.Encoding);
        Assert.Equal(new byte[] { 0xFE, 0xFF, 0x72, 0xAC }, WorkspaceTextCodec.Encode(bigEndian, "犬"));

        var utf8Bom = WorkspaceTextCodec.Decode([0xEF, 0xBB, 0xBF, 0x61]);
        Assert.Equal("a", utf8Bom.Content);
        Assert.Equal("utf-8", utf8Bom.Encoding);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF, 0x62 }, WorkspaceTextCodec.Encode(utf8Bom, "b"));
        var error = Assert.Throws<WorkspaceTextDecodeException>(() => WorkspaceTextCodec.Decode([0xC3, 0x28]));
        Assert.Equal("WORKSPACE_BINARY_OR_UNSUPPORTED_ENCODING", error.Code);
    }

    [Fact]
    public async Task Glob_matches_nested_paths_and_grep_returns_literal_and_regex_lines()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(Path.Combine(fixture.Root, "subdir"));
        File.WriteAllText(Path.Combine(fixture.Root, "subdir", "match.txt"),
            "noise\nneedle value\nline 42\n", new UTF8Encoding(false));

        var glob = await Invoke(Tool(fixture, "workspace.glob"), fixture.Scope,
            new { pattern = "**/match.txt" });
        Assert.True(glob.Success);
        var globItem = Assert.Single(glob.Payload.GetProperty("items").EnumerateArray());
        Assert.Equal("subdir/match.txt", globItem.GetProperty("path").GetString());
        Assert.Equal("file", globItem.GetProperty("kind").GetString());
        Assert.False(glob.Payload.GetProperty("truncated").GetBoolean());

        var literal = await Invoke(Tool(fixture, "workspace.grep"), fixture.Scope,
            new { path = "subdir", pattern = "needle", mode = "literal" });
        var literalItem = Assert.Single(literal.Payload.GetProperty("items").EnumerateArray());
        Assert.Equal(2, literalItem.GetProperty("lineNumber").GetInt32());
        Assert.Equal("needle value", literalItem.GetProperty("line").GetString());

        var regex = await Invoke(Tool(fixture, "workspace.grep"), fixture.Scope,
            new { path = "subdir", pattern = "^line \\d+$", mode = "regex" });
        var regexItem = Assert.Single(regex.Payload.GetProperty("items").EnumerateArray());
        Assert.Equal(3, regexItem.GetProperty("lineNumber").GetInt32());
        Assert.Equal("line 42", regexItem.GetProperty("line").GetString());
    }

    [Fact]
    public async Task Read_and_listing_enforce_input_and_serialized_output_limits()
    {
        using var fixture = new Fixture();
        var readTool = Tool(fixture, "workspace.read");
        File.WriteAllBytes(Path.Combine(fixture.Root, "too-large.txt"), new byte[1024 * 1024 + 1]);
        var tooLarge = await Invoke(readTool, fixture.Scope, new { path = "too-large.txt" });
        Assert.False(tooLarge.Success);
        Assert.Equal("WORKSPACE_FILE_TOO_LARGE", tooLarge.ErrorCode);
        Assert.Equal(ToolExecutionState.NotExecuted, tooLarge.ExecutionState);

        for (var index = 0; index < 100; index++)
            File.WriteAllBytes(Path.Combine(fixture.Root, new string('界', 120) + index.ToString("D3") + ".txt"), []);

        var listing = await Invoke(Tool(fixture, "workspace.list"), fixture.Scope, new { path = "." });
        Assert.True(listing.Success);
        Assert.True(listing.Payload.GetProperty("truncated").GetBoolean());
        Assert.InRange(listing.Payload.GetProperty("items").GetArrayLength(), 1, 100);
        Assert.InRange(Encoding.UTF8.GetByteCount(listing.Payload.GetRawText()), 1, 32 * 1024);
        Assert.Contains(listing.Payload.GetProperty("items").EnumerateArray(), item =>
            item.GetProperty("kind").GetString() == "file" && item.GetProperty("path").GetString()!.Contains('界'));
    }

    [Fact]
    public async Task Recursive_scan_stops_at_entry_depth_and_input_byte_budgets()
    {
        using var fixture = new Fixture();
        for (var index = 0; index < 2050; index++)
            File.WriteAllBytes(Path.Combine(fixture.Root, $"entry-{index:D4}.txt"), []);

        var counter = new CountingGuard(fixture.Guard);
        var grep = Assert.Single(WorkspaceReadTools.Create(counter), tool => tool.Descriptor.Name == "workspace.grep");
        var byEntries = await Invoke(grep, fixture.Scope, new { pattern = "no-match", mode = "literal" });
        Assert.True(byEntries.Success);
        Assert.True(byEntries.Payload.GetProperty("truncated").GetBoolean());
        Assert.InRange(counter.OpenCount, 1, 2049);

        using var byteFixture = new Fixture();
        var body = new byte[1024 * 1024];
        Encoding.UTF8.GetBytes("needle\n").CopyTo(body, 0);
        Array.Fill(body, (byte)'x', 7, body.Length - 7);
        for (var index = 0; index < 9; index++)
            File.WriteAllBytes(Path.Combine(byteFixture.Root, $"large-{index}.txt"), body);

        var byteScan = await Invoke(Tool(byteFixture, "workspace.grep"), byteFixture.Scope,
            new { pattern = "needle", mode = "literal" });
        Assert.True(byteScan.Success);
        Assert.True(byteScan.Payload.GetProperty("truncated").GetBoolean());
        Assert.InRange(byteScan.Payload.GetProperty("items").GetArrayLength(), 1, 8);
        Assert.All(byteScan.Payload.GetProperty("items").EnumerateArray(), item =>
        {
            Assert.Equal(1, item.GetProperty("lineNumber").GetInt32());
            Assert.Equal("needle", item.GetProperty("line").GetString());
        });

        using var depthFixture = new Fixture();
        var directory = depthFixture.Root;
        for (var index = 0; index < 17; index++)
        {
            directory = Path.Combine(directory, $"level-{index:D2}");
            Directory.CreateDirectory(directory);
        }
        var byDepth = await Invoke(Tool(depthFixture, "workspace.glob"), depthFixture.Scope,
            new { pattern = "**/*", path = "." });
        Assert.True(byDepth.Success);
        Assert.True(byDepth.Payload.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task Reparse_points_and_unsafe_paths_return_not_executed_without_path_details()
    {
        using var fixture = new Fixture();
        var outside = Path.Combine(Path.GetTempPath(), "fgo-workspace-read-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        var link = Path.Combine(fixture.Root, "linked");
        try
        {
            File.WriteAllText(Path.Combine(outside, "secret.txt"), "private");
            CreateJunction(link, outside);

            var result = await Invoke(Tool(fixture, "workspace.list"), fixture.Scope, new { path = "." });

            Assert.False(result.Success);
            Assert.Equal("WORKSPACE_REPARSE_DENIED", result.ErrorCode);
            Assert.Equal(ToolExecutionState.NotExecuted, result.ExecutionState);
            Assert.Equal("{}", result.Payload.GetRawText());
            Assert.DoesNotContain(outside, result.Payload.GetRawText(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            if (Directory.Exists(outside)) Directory.Delete(outside, recursive: true);
        }

        var denied = await Invoke(Tool(fixture, "workspace.read"), fixture.Scope, new { path = "../outside.txt" });
        Assert.False(denied.Success);
        Assert.Equal("WORKSPACE_PATH_DENIED", denied.ErrorCode);
        Assert.Equal(ToolExecutionState.NotExecuted, denied.ExecutionState);
        Assert.Equal("{}", denied.Payload.GetRawText());
    }

    [Fact]
    public async Task Cancellation_and_invalid_patterns_fail_with_safe_metadata()
    {
        using var fixture = new Fixture();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var grep = Tool(fixture, "workspace.grep");

        var stopped = await Invoke(grep, fixture.Scope, new { pattern = "needle" }, canceled.Token);
        Assert.False(stopped.Success);
        Assert.Equal("TOOL_CANCELLED", stopped.ErrorCode);
        Assert.Equal(ToolExecutionState.NotExecuted, stopped.ExecutionState);
        Assert.Equal("{}", stopped.Payload.GetRawText());

        var invalid = await Invoke(grep, fixture.Scope, new { pattern = "[unbounded", mode = "regex" });
        Assert.False(invalid.Success);
        Assert.Equal("TOOL_INVALID_PATTERN", invalid.ErrorCode);
        Assert.Equal(ToolExecutionState.NotExecuted, invalid.ExecutionState);
        Assert.DoesNotContain("Exception", invalid.Payload.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    private static IToolProvider Tool(Fixture fixture, string name) =>
        Assert.Single(WorkspaceReadTools.Create(fixture.Guard), tool => tool.Descriptor.Name == name);

    private static ValueTask<ToolResult> Invoke(IToolProvider tool, ToolScope scope, object arguments,
        CancellationToken token = default) =>
        tool.InvokeAsync(new ToolInvocation(scope, JsonSerializer.SerializeToElement(arguments)), token);

    private static void CreateJunction(string link, string target)
    {
        var start = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { "/d", "/c", "mklink", "/J", link, target })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        Assert.True(process.WaitForExit(5000));
        Assert.Equal(0, process.ExitCode);
    }

    private sealed class Fixture : IWorkspaceAuthorizationSource, IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "fgo-workspace-read-" + Guid.NewGuid().ToString("N"));
        public ToolScope Scope { get; } = new("synthetic-conversation", "synthetic-role", "synthetic-project");
        public WorkspaceAuthorization Current { get; set; }
        public WorkspaceAccessGuard Guard { get; }

        public Fixture()
        {
            Directory.CreateDirectory(Root);
            Current = new(Scope, new("synthetic-workspace", 1), Root, Root);
            Guard = new WorkspaceAccessGuard(this);
        }

        public WorkspaceAuthorization? GetCurrent(ToolScope scope) => scope == Scope ? Current : null;

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class CountingGuard(IWorkspaceAccessGuard inner) : IWorkspaceAccessGuard
    {
        public int OpenCount { get; private set; }
        public WorkspaceAuthorization GetAuthorization(ToolScope scope) => inner.GetAuthorization(scope);
        public IWorkspacePathLease Open(ToolScope scope, string path, WorkspacePathKind kind, bool allowCreate = false)
        {
            OpenCount++;
            return inner.Open(scope, path, kind, allowCreate);
        }
    }

    private sealed class RevokingGuard(Fixture fixture) : IWorkspaceAccessGuard
    {
        public WorkspaceAuthorization GetAuthorization(ToolScope scope) => fixture.Guard.GetAuthorization(scope);

        public IWorkspacePathLease Open(ToolScope scope, string path, WorkspacePathKind kind, bool allowCreate = false) =>
            new RevokingLease(fixture, fixture.Guard.Open(scope, path, kind, allowCreate));
    }

    private sealed class RevokingLease(Fixture fixture, IWorkspacePathLease inner) : IWorkspacePathLease
    {
        private int _revalidations;
        public WorkspaceAuthorization Authority => inner.Authority;
        public string FullPath => inner.FullPath;
        public bool Exists => inner.Exists;
        public void ValidateOpenedFile(FileStream file) => inner.ValidateOpenedFile(file);

        public void Revalidate()
        {
            if (++_revalidations == 2)
                fixture.Current = fixture.Current with { Authorization = new("synthetic-workspace", 2) };
            inner.Revalidate();
        }

        public void Dispose() => inner.Dispose();
    }
}
