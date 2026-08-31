using System.Text.Json;
using FgoPet.AgentProtocol.Messages;

namespace FgoPet.CodexAdapter.AppServer;

public interface ICodexTargetResolver
{
    string Resolve(string targetId);
    bool IsReadOnly(string targetId) => false;
}

public interface ICodexAppServerRpc
{
    Task<JsonElement> CallAsync(string method, object parameters, CancellationToken cancellationToken = default);
}

public sealed record CodexStartedTask(string TaskId, string? TurnId = null);

public sealed class CodexAppServerClient
{
    private readonly ICodexAppServerRpc _rpc;
    private readonly ICodexTargetResolver _targets;

    public CodexAppServerClient(ICodexAppServerRpc rpc, ICodexTargetResolver targets)
    {
        _rpc = rpc ?? throw new ArgumentNullException(nameof(rpc));
        _targets = targets ?? throw new ArgumentNullException(nameof(targets));
    }

    public async Task<CodexStartedTask> StartTaskAsync(
        DispatchTaskRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var cwd = _targets.Resolve(request.TargetId);
        var readOnly = _targets.IsReadOnly(request.TargetId);
        var thread = await _rpc.CallAsync(
            "thread/start",
            new { cwd, approvalPolicy = "on-request", sandbox = readOnly ? "read-only" : "workspace-write", serviceName = "fgo-pet" },
            cancellationToken).ConfigureAwait(false);
        var taskId = thread.GetProperty("thread").GetProperty("id").GetString();
        if (string.IsNullOrWhiteSpace(taskId))
        {
            throw new InvalidOperationException("Codex App Server did not return a thread ID.");
        }

        var input = string.IsNullOrWhiteSpace(request.Description)
            ? request.Title
            : $"{request.Title}\n\n{request.Description}";
        object sandboxPolicy = readOnly
            ? new { type = "readOnly", networkAccess = false }
            : new { type = "workspaceWrite", writableRoots = new[] { cwd }, networkAccess = false, excludeSlashTmp = true, excludeTmpdirEnvVar = true };
        var turn = await _rpc.CallAsync(
            "turn/start",
            new { threadId = taskId, input = new[] { new { type = "text", text = input } }, cwd, approvalPolicy = "on-request", sandboxPolicy },
            cancellationToken).ConfigureAwait(false);
        var turnId = turn.GetProperty("turn").GetProperty("id").GetString();
        if (string.IsNullOrWhiteSpace(turnId)) throw new InvalidDataException("codex_turn_missing");
        return new CodexStartedTask(taskId, turnId);
    }

}
