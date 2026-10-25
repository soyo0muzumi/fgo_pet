using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FgoPet.Extensibility;
using FgoPet.Plugin.Workspace;
using Xunit;

namespace FgoPet.Capability.Tests.Workspace;

public sealed class WorkspaceWriteToolsTests
{
    [Fact]
    public async Task New_file_create_and_existing_conflict_have_proven_effect_states()
    {
        using var f = new WorkspaceAccessGuardTests.Fixture();
        var tool = WorkspaceWriteTools.Create(f.Guard).Single(t => t.Descriptor.Name == "workspace.write");
        var result = await Invoke(tool, f.Scope, new { path = "new.txt", content = "你好🌟", mustNotExist = true });
        Assert.True(result.Success);
        Assert.Equal(ToolExecutionState.Committed, result.ExecutionState);
        Assert.Equal("你好🌟", File.ReadAllText(Path.Combine(f.Root, "new.txt")));
        result = await Invoke(tool, f.Scope, new { path = "new.txt", content = "other", mustNotExist = true });
        Assert.False(result.Success);
        Assert.Equal(ToolExecutionState.NotExecuted, result.ExecutionState);
        Assert.Equal("CONFLICT", result.ErrorCode);
    }

    [Fact]
    public async Task Overwrite_requires_expected_version_and_preserves_encoding()
    {
        using var f = new WorkspaceAccessGuardTests.Fixture();
        var path = Path.Combine(f.Root, "fixture.txt");
        File.WriteAllText(path, "before\r\n", Encoding.Unicode);
        var original = File.ReadAllBytes(path);
        var version = Convert.ToHexString(SHA256.HashData(original));
        var tool = WorkspaceWriteTools.Create(f.Guard).Single(t => t.Descriptor.Name == "workspace.write");
        var denied = await Invoke(tool, f.Scope, new { path = "fixture.txt", content = "after\r\n", mustNotExist = false, expectedVersion = new string('A', 64) });
        Assert.Equal("CONFLICT", denied.ErrorCode);
        Assert.Equal(original, File.ReadAllBytes(path));
        var result = await Invoke(tool, f.Scope, new { path = "fixture.txt", content = "after\r\n", mustNotExist = false, expectedVersion = version });
        Assert.True(result.Success);
        Assert.Equal(new byte[] { 0xFF, 0xFE }, File.ReadAllBytes(path)[..2]);
        Assert.Equal("after\r\n", File.ReadAllText(path, Encoding.Unicode));
    }

    [Theory]
    [InlineData("fixture", "changed", true)]
    [InlineData("missing", "changed", false)]
    [InlineData("", "changed", false)]
    public async Task Edit_requires_nonempty_unique_match(string oldText, string newText, bool success)
    {
        using var f = new WorkspaceAccessGuardTests.Fixture();
        var path = Path.Combine(f.Root, "fixture.txt");
        var version = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        var tool = WorkspaceWriteTools.Create(f.Guard).Single(t => t.Descriptor.Name == "workspace.edit");
        var result = await Invoke(tool, f.Scope, new { path = "fixture.txt", oldText, newText, expectedVersion = version });
        Assert.Equal(success, result.Success);
        Assert.Equal(success ? "changed" : "fixture", File.ReadAllText(path));
    }

    [Fact]
    public async Task Multiple_matches_and_missing_execution_context_do_not_modify_file()
    {
        using var f = new WorkspaceAccessGuardTests.Fixture();
        var path = Path.Combine(f.Root, "fixture.txt");
        File.WriteAllText(path, "xx xx");
        var version = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        var edit = WorkspaceWriteTools.Create(f.Guard).Single(t => t.Descriptor.Name == "workspace.edit");
        var result = await Invoke(edit, f.Scope, new { path = "fixture.txt", oldText = "xx", newText = "yy", expectedVersion = version });
        Assert.False(result.Success);
        Assert.Equal("xx xx", File.ReadAllText(path));
        var args = JsonSerializer.SerializeToElement(new { path = "fixture.txt", content = "other", mustNotExist = false, expectedVersion = version });
        var write = WorkspaceWriteTools.Create(f.Guard).Single(t => t.Descriptor.Name == "workspace.write");
        result = await write.InvokeAsync(new(f.Scope, args), default);
        Assert.Equal(ToolExecutionState.NotExecuted, result.ExecutionState);
        Assert.Equal("xx xx", File.ReadAllText(path));
    }

    private static ValueTask<ToolResult> Invoke(IToolProvider tool, ToolScope scope, object arguments) =>
        tool.InvokeAsync(new(scope, JsonSerializer.SerializeToElement(arguments)) {
            ExecutionContext = new("run", 1, "call", new string('A', 64)) {
                ResourceAuthorization = ((IToolResourceAuthorizationProvider)tool).GetAuthorization(scope) } }, default);
}
