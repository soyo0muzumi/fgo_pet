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
        IBoundedProcessRunner runner = Environment.GetEnvironmentVariable("FGO_PET_SHELL_STAGE_DIAGNOSTICS") == "1"
            ? new StagedSyntheticRunner() : new WindowsBoundedProcessRunner();
        var tool = new ShellPlugin(f.Guard, runner).Contributions.Tools.Single();
        var result = await Invoke(tool, f.Scope, new { command = "[Console]::Write('你好🌟')", timeoutSeconds = 5 });
        // Safe outcome metadata is constructed after the bounded operation; no output text or command is logged.
        var outcome = JsonSerializer.Serialize(new {
            stage = "unicode_result", result.ErrorCode, executionState = result.ExecutionState?.ToString(),
            status = result.Payload.TryGetProperty("status", out var status) ? status.GetString() : null,
            elapsedMilliseconds = result.Payload.TryGetProperty("elapsedMilliseconds", out var elapsed) ? (long?)elapsed.GetInt64() : null,
            exitCode = result.Payload.TryGetProperty("exitCode", out var code) && code.ValueKind == JsonValueKind.Number ? (int?)code.GetInt32() : null,
            stdoutCharacters = result.Payload.TryGetProperty("stdout", out var stdout) ? stdout.GetString()?.Length : null,
            stderrCharacters = result.Payload.TryGetProperty("stderr", out var stderr) ? stderr.GetString()?.Length : null,
            syntheticStages = ReadSyntheticStages(result.Payload), stopwatchFrequency = System.Diagnostics.Stopwatch.Frequency,
            modulePreparationProgress = result.Payload.TryGetProperty("stderr", out var stageOutput) &&
                (stageOutput.GetString() ?? "").Contains("Preparing modules for first use", StringComparison.OrdinalIgnoreCase),
        });
        if (Environment.GetEnvironmentVariable("FGO_PET_SHELL_STAGE_DIAGNOSTICS") == "1")
            Console.WriteLine("SHELL_STAGE_METADATA_ONLY " + outcome);
        Assert.True(result.Success, outcome);
        Assert.Equal("你好🌟", result.Payload.GetProperty("stdout").GetString());
        result = await Invoke(tool, f.Scope, new { command = "Start-Sleep -Seconds 30", timeoutSeconds = 1 });
        Assert.Equal(ToolExecutionState.Unknown, result.ExecutionState);
        Assert.Equal(JsonValueKind.Null, result.Payload.GetProperty("exitCode").ValueKind);
    }

    private static ValueTask<ToolResult> Invoke(IToolProvider tool, ToolScope scope, object arguments) =>
        tool.InvokeAsync(new(scope, JsonSerializer.SerializeToElement(arguments)) { ExecutionContext = new("run", 1, "call", new string('A', 64)) {
            ResourceAuthorization = ((IToolResourceAuthorizationProvider)tool).GetAuthorization(scope) } }, default);
    private sealed class StagedSyntheticRunner : IBoundedProcessRunner
    {
        private readonly WindowsBoundedProcessRunner inner = new();
        public ValueTask<BoundedProcessResult> RunAsync(BoundedProcessRequest request, CancellationToken token)
        {
            const string prefix = "[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false); ";
            var script = System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(request.Arguments[^1]));
            if (!script.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidOperationException("SYNTHETIC_SHELL_PREFIX_CHANGED");
            var staged = "[Console]::Error.WriteLine('P4_ENGINE_ENTERED:' + [System.Diagnostics.Stopwatch]::GetTimestamp()); " + prefix +
                "[Console]::Error.WriteLine('P4_ENCODING_READY:' + [System.Diagnostics.Stopwatch]::GetTimestamp()); " + script[prefix.Length..] +
                "; [Console]::Error.WriteLine('P4_COMMAND_DONE:' + [System.Diagnostics.Stopwatch]::GetTimestamp());";
            var arguments = request.Arguments.SetItem(request.Arguments.Length - 1,
                Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(staged)));
            return inner.RunAsync(request with { Arguments = arguments }, token);
        }
    }
    private sealed record SyntheticStage(string Stage, long Tick);
    private static SyntheticStage[] ReadSyntheticStages(JsonElement payload)
    {
        if (!payload.TryGetProperty("stderr", out var value)) return [];
        var stages = new List<SyntheticStage>();
        foreach (var line in (value.GetString() ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf(':');
            if (separator < 0) continue;
            var stage = line[..separator];
            if (stage is not ("P4_ENGINE_ENTERED" or "P4_ENCODING_READY" or "P4_COMMAND_DONE")) continue;
            if (long.TryParse(line[(separator + 1)..], System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var tick)) stages.Add(new(stage, tick));
        }
        return stages.ToArray();
    }
    private sealed class RecordingRunner : IBoundedProcessRunner
    {
        public int Calls { get; private set; }
        public BoundedProcessRequest? Request { get; private set; }
        public BoundedProcessResult Result { get; init; } = new(true, BoundedProcessExit.Completed, 0, "", "", false);
        public ValueTask<BoundedProcessResult> RunAsync(BoundedProcessRequest request, CancellationToken token)
        { Calls++; Request = request; request.BeforeStart?.Invoke(); return ValueTask.FromResult(Result); }
    }
}
