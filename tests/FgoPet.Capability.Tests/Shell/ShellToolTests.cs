using System.Text.Json;
using FgoPet.Extensibility;
using FgoPet.Platform.Processes;
using FgoPet.Platform.Windows.Processes;
using FgoPet.Plugin.Shell;
using FgoPet.Capability.Tests.Workspace;
using Xunit;

namespace FgoPet.Capability.Tests.Shell;

public sealed class ShellToolTests
{
    [Fact]
    public async Task Missing_frozen_authority_or_outside_cwd_never_launches()
    {
        using var f = new WorkspaceAccessGuardTests.Fixture();
        var runner = new RecordingRunner();
        var tool = new ShellPlugin(f.Guard, runner).Contributions.Tools.Single();
        var arguments = JsonSerializer.SerializeToElement(new { command = "exit 0" });
        Assert.False((await tool.InvokeAsync(new(f.Scope, arguments), default)).Success);
        var invocation = new ToolInvocation(f.Scope, arguments) { ExecutionContext = new("run", 1, "call", new string('A', 64)) };
        Assert.Equal("WORKSPACE_SCOPE_CHANGED", (await tool.InvokeAsync(invocation, default)).ErrorCode);
        var result = await Invoke(tool, f.Scope, new { command = "exit 0", cwd = "../outside" });
        Assert.Equal(ToolExecutionState.NotExecuted, result.ExecutionState);
        Assert.Equal(0, runner.Calls);
    }

    [Theory]
    [InlineData(BoundedProcessExit.Completed, 0, ToolExecutionState.Committed, true)]
    [InlineData(BoundedProcessExit.Completed, 3, ToolExecutionState.Committed, false)]
    [InlineData(BoundedProcessExit.TimedOut, null, ToolExecutionState.Unknown, false)]
    [InlineData(BoundedProcessExit.Cancelled, null, ToolExecutionState.Unknown, false)]
    [InlineData(BoundedProcessExit.LaunchFailed, null, ToolExecutionState.NotExecuted, false)]
    public async Task Result_preserves_execution_evidence_without_fabricated_exit(BoundedProcessExit exit, int? code,
        ToolExecutionState state, bool success)
    {
        using var f = new WorkspaceAccessGuardTests.Fixture();
        var runner = new RecordingRunner { Result = new(exit != BoundedProcessExit.LaunchFailed, exit, code, "fixture", "", false) };
        var tool = new ShellPlugin(f.Guard, runner).Contributions.Tools.Single();
        var result = await Invoke(tool, f.Scope, new { command = "exit 0", cwd = ".", timeoutSeconds = 5 });
        Assert.Equal(state, result.ExecutionState);
        Assert.Equal(success, result.Success);
        Assert.Equal(code, result.Payload.GetProperty("exitCode").ValueKind == JsonValueKind.Null ? (int?)null : result.Payload.GetProperty("exitCode").GetInt32());
        Assert.Equal(f.Root, runner.Request!.WorkingDirectory);
        Assert.Equal(TimeSpan.FromSeconds(5), runner.Request.Timeout);
        Assert.NotNull(runner.Request.BeforeStart);
        Assert.Equal(24, runner.Request.Correlation!.Length);
    }

    [Fact]
    public async Task Real_Powershell_returns_unicode_bounded_output_and_null_exit_on_timeout()
    {
        using var f = new WorkspaceAccessGuardTests.Fixture();
        var tool = new ShellPlugin(f.Guard, new WindowsBoundedProcessRunner()).Contributions.Tools.Single();
        var result = await Invoke(tool, f.Scope, new { command = "[Console]::Write('你好🌟')", timeoutSeconds = 5 });
        Assert.True(result.Success);
        Assert.Equal("你好🌟", result.Payload.GetProperty("stdout").GetString());
        result = await Invoke(tool, f.Scope, new { command = "Start-Sleep -Seconds 30", timeoutSeconds = 1 });
        Assert.Equal(ToolExecutionState.Unknown, result.ExecutionState);
        Assert.Equal(JsonValueKind.Null, result.Payload.GetProperty("exitCode").ValueKind);
    }

    private static ValueTask<ToolResult> Invoke(IToolProvider tool, ToolScope scope, object arguments) =>
        tool.InvokeAsync(new(scope, JsonSerializer.SerializeToElement(arguments)) { ExecutionContext = new("run", 1, "call", new string('A', 64)) {
            ResourceAuthorization = ((IToolResourceAuthorizationProvider)tool).GetAuthorization(scope) } }, default);
    private sealed class RecordingRunner : IBoundedProcessRunner
    {
        public int Calls { get; private set; }
        public BoundedProcessRequest? Request { get; private set; }
        public BoundedProcessResult Result { get; init; } = new(true, BoundedProcessExit.Completed, 0, "", "", false);
        public ValueTask<BoundedProcessResult> RunAsync(BoundedProcessRequest request, CancellationToken token)
        { Calls++; Request = request; request.BeforeStart?.Invoke(); return ValueTask.FromResult(Result); }
    }
}
