using System.Text.Json;
using FgoPet.AgentProtocol.Messages;

namespace FgoPet.CodexAdapter.AppServer;

public interface ICodexTargetResolver
{
    string Resolve(string targetId);
}

public interface ICodexAppServerRpc
{
    Task<JsonElement> CallAsync(string method, object parameters, CancellationToken cancellationToken = default);
}

public sealed record CodexStartedTask(string TaskId);

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
        var thread = await _rpc.CallAsync(
            "thread/start",
            new { cwd },
            cancellationToken).ConfigureAwait(false);
        var taskId = ReadString(thread, "thread_id") ?? ReadString(thread, "id");
        if (string.IsNullOrWhiteSpace(taskId))
        {
            throw new InvalidOperationException("Codex App Server did not return a thread ID.");
        }

        var input = string.IsNullOrWhiteSpace(request.Description)
            ? request.Title
            : $"{request.Title}\n\n{request.Description}";
        await _rpc.CallAsync(
            "turn/start",
            new { thread_id = taskId, input },
            cancellationToken).ConfigureAwait(false);
        return new CodexStartedTask(taskId);
    }

    private static string? ReadString(JsonElement value, string propertyName)
    {
        if (value.TryGetProperty(propertyName, out var direct) && direct.ValueKind == JsonValueKind.String)
        {
            return direct.GetString();
        }

        return value.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object
            ? ReadString(result, propertyName)
            : null;
    }
}

public sealed class JsonRpcLineClient : ICodexAppServerRpc, IAsyncDisposable
{
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _nextId;

    public JsonRpcLineClient(Stream input, Stream output)
    {
        _reader = new StreamReader(input ?? throw new ArgumentNullException(nameof(input)), leaveOpen: true);
        _writer = new StreamWriter(output ?? throw new ArgumentNullException(nameof(output))) { AutoFlush = true };
    }

    public async Task<JsonElement> CallAsync(string method, object parameters, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var id = Interlocked.Increment(ref _nextId);
            await _writer.WriteLineAsync(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id,
                method,
                @params = parameters,
            })).ConfigureAwait(false);
            while (await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!root.TryGetProperty("id", out var responseId) || responseId.GetInt64() != id)
                {
                    continue;
                }

                if (root.TryGetProperty("error", out var error))
                {
                    throw new InvalidOperationException(error.ToString());
                }

                return root.TryGetProperty("result", out var result)
                    ? result.Clone()
                    : root.Clone();
            }

            throw new EndOfStreamException("Codex App Server closed the RPC stream.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        _gate.Dispose();
        _reader.Dispose();
        _writer.Dispose();
        return ValueTask.CompletedTask;
    }
}
