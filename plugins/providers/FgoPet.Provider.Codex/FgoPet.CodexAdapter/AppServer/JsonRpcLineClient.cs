using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using FgoPet.AgentRuntime.Pipes;

namespace FgoPet.CodexAdapter.AppServer;

/// <summary>One reader separates replies from streamed task notifications.</summary>
public sealed class JsonRpcLineClient : ICodexAppServerRpc, IAsyncDisposable
{
    private readonly Stream _output;
    private readonly JsonLineFrameReader _reader;
    private readonly SemaphoreSlim _writes = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _calls = new();
    private readonly Channel<JsonElement> _notifications = Channel.CreateBounded<JsonElement>(256);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _reading;
    private long _nextId;
    private int _closed;

    public JsonRpcLineClient(Stream input, Stream output)
    {
        _reader = new JsonLineFrameReader(input);
        _output = output;
        _reading = ReadAsync();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await CallAsync("initialize", new { clientInfo = new { name = "fgo_pet", title = "FGO Pet", version = "1.0.0" } }, cancellationToken).ConfigureAwait(false);
        await WriteAsync(new { method = "initialized", @params = new { } }, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<JsonElement> ReadNotificationAsync(CancellationToken cancellationToken) => _notifications.Reader.ReadAsync(cancellationToken);

    public async Task<JsonElement> CallAsync(string method, object parameters, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _calls[id] = completion;
        try
        {
            if (Volatile.Read(ref _closed) != 0) throw new EndOfStreamException("codex_rpc_closed");
            await WriteAsync(new { id, method, @params = parameters }, timeout.Token).ConfigureAwait(false);
            return await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        finally { _calls.TryRemove(id, out _); }
    }

    private async Task ReadAsync()
    {
        Exception failure = new EndOfStreamException("codex_rpc_closed");
        try
        {
            while (await _reader.ReadAsync(_lifetime.Token).ConfigureAwait(false) is { } line)
            {
                using var document = JsonDocument.Parse(line);
                var message = document.RootElement;
                if (message.TryGetProperty("method", out var method))
                {
                    var name = method.GetString();
                    if (message.TryGetProperty("id", out var requestId))
                    {
                        // Approval requests are surfaced to the host so it can open a visible
                        // Codex session. Never auto-approve or auto-cancel them: the child
                        // app-server is intentionally torn down after the handoff.
                        if (name is "item/commandExecution/requestApproval" or "item/fileChange/requestApproval")
                        {
                            var approval = JsonSerializer.SerializeToElement(new
                            {
                                method = "fgo/approvalRequired",
                                @params = new { method = name, requestId = requestId.Clone() },
                            });
                            if (!_notifications.Writer.TryWrite(approval))
                                throw new InvalidDataException("codex_notifications_overflow");
                        }
                        else
                        {
                            await WriteAsync(new { id = requestId.Clone(), error = new { code = -32601, message = "interactive_approval_required" } }, _lifetime.Token).ConfigureAwait(false);
                        }
                    }
                    else if (name is "turn/completed" or "turn/started" or "item/started" or "error")
                    {
                        if (!_notifications.Writer.TryWrite(message.Clone())) throw new InvalidDataException("codex_notifications_overflow");
                    }
                }
                else if (message.TryGetProperty("id", out var id) && id.TryGetInt64(out var number) && _calls.TryGetValue(number, out var completion))
                {
                    if (message.TryGetProperty("error", out _)) completion.TrySetException(new IOException("codex_rpc_rejected"));
                    else if (message.TryGetProperty("result", out var result)) completion.TrySetResult(result.Clone());
                    else completion.TrySetException(new InvalidDataException("codex_rpc_invalid_response"));
                }
            }
        }
        catch (Exception error) { failure = error is OperationCanceledException ? error : new IOException("codex_rpc_unavailable"); }
        finally
        {
            Volatile.Write(ref _closed, 1);
            _notifications.Writer.TryComplete(failure);
            foreach (var call in _calls.Values) call.TrySetException(failure);
        }
    }

    private async Task WriteAsync(object message, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message) + "\n");
        if (bytes.Length > JsonLinePipeClient.MaxFrameBytes) throw new InvalidDataException("codex_rpc_frame_too_large");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await _writes.WaitAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            await _output.WriteAsync(bytes, timeout.Token).ConfigureAwait(false);
            await _output.FlushAsync(timeout.Token).ConfigureAwait(false);
        }
        finally { _writes.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _reading.ConfigureAwait(false);
        _lifetime.Dispose();
    }
}
