using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using FgoPet.Extensibility;
using FgoPet.Platform.Processes;

namespace FgoPet.Plugin.Shell;

/// <summary>Host PowerShell capability. Workspace authority constrains cwd, not process permissions.</summary>
public sealed class ShellPlugin : IFgoPetPlugin, IDisposable
{
    public PluginManifest Manifest { get; } = new("shell", "1.0.0", 1, []);
    public PluginContributions Contributions { get; }
    public ShellPlugin(IWorkspaceAccessGuard guard, IBoundedProcessRunner runner) =>
        Contributions = PluginContributions.Empty with { Tools = [new ShellTool(guard, runner)] };
    public ValueTask StartAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }
    public ValueTask StopAsync(CancellationToken token) => ValueTask.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public void Dispose() { }

    private sealed class ShellTool(IWorkspaceAccessGuard guard, IBoundedProcessRunner runner) : IToolProvider, IToolResourceAuthorizationProvider
    {
        private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        public ToolDescriptor Descriptor { get; } = new("shell.exec",
            "Execute one explicitly approved noninteractive PowerShell command with host-user permissions. cwd is constrained to the workspace; this is not a sandbox.", """
            {"type":"object","properties":{"command":{"type":"string","minLength":1,"maxLength":6000},
            "cwd":{"type":"string","minLength":1,"maxLength":1024},"timeoutSeconds":{"type":"integer","minimum":1,"maximum":120}},
            "required":["command"],"additionalProperties":false}
            """, ToolEffect.Command);
        public ToolResourceAuthorization GetAuthorization(ToolScope scope) => guard.GetAuthorization(scope).Authorization;
        public async ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken token)
        {
            if (invocation.ExecutionContext is null) return Failure("TOOL_AUTHORIZATION_DENIED");
            var attemptedLaunch = false;
            try
            {
                token.ThrowIfCancellationRequested();
                var args = invocation.Arguments;
                var command = args.GetProperty("command").GetString();
                var cwd = args.TryGetProperty("cwd", out var directory) ? directory.GetString() : ".";
                var timeout = args.TryGetProperty("timeoutSeconds", out var seconds) ? seconds.GetInt32() : 30;
                if (string.IsNullOrWhiteSpace(command) || command.Length > 6000 || command.Contains('\0') || timeout is < 1 or > 120)
                    return Failure("TOOL_INVALID_ARGUMENTS");
                using var lease = guard.Open(invocation.Scope, cwd!, WorkspacePathKind.Directory);
                if (invocation.ExecutionContext.ResourceAuthorization != lease.Authority.Authorization) return Failure("WORKSPACE_SCOPE_CHANGED");
                lease.Revalidate();
                var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
                var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(
                    "[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false); " + command));
                var watch = Stopwatch.StartNew();
                attemptedLaunch = true;
                var result = await runner.RunAsync(new BoundedProcessRequest(executable, ["-NoProfile", "-NonInteractive", "-EncodedCommand", encoded],
                    lease.FullPath, TimeSpan.FromSeconds(timeout), MaxOutputBytes: 16384) { BeforeStart = lease.Revalidate,
                        Correlation = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                            Encoding.UTF8.GetBytes(invocation.ExecutionContext.IdempotencyKey)))[..24] }, token);
                var known = result.Started && result.Exit == BoundedProcessExit.Completed && result.ExitCode is not null;
                return new(known && result.ExitCode == 0, JsonSerializer.SerializeToElement(new {
                    exitCode = known ? result.ExitCode : null, stdout = result.StandardOutput, stderr = result.StandardError,
                    truncated = result.Truncated, elapsedMilliseconds = watch.ElapsedMilliseconds, status = result.Exit.ToString() }, Json),
                    known ? result.ExitCode == 0 ? null : "SHELL_EXIT_FAILED" : result.Started ? "TOOL_EXECUTION_UNKNOWN" : "SHELL_NOT_STARTED")
                    { ExecutionState = known ? ToolExecutionState.Committed : result.Started ? ToolExecutionState.Unknown : ToolExecutionState.NotExecuted };
            }
            catch (WorkspaceAccessException error) { return Failure(error.Code); }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or KeyNotFoundException or JsonException)
            { return attemptedLaunch ? new(false, JsonSerializer.SerializeToElement(new { exitCode = (int?)null }), "TOOL_EXECUTION_UNKNOWN")
                { ExecutionState = ToolExecutionState.Unknown } : Failure("TOOL_INVALID_ARGUMENTS"); }
        }
        private static ToolResult Failure(string code) => new(false, JsonSerializer.SerializeToElement(new { }), code)
            { ExecutionState = ToolExecutionState.NotExecuted };
    }
}
