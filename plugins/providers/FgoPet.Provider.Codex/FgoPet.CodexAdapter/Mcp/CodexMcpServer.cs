using System.Text.Json;
using FgoPet.AgentProtocol;
using FgoPet.AgentProtocol.Messages;
using FgoPet.AgentProtocol.Privacy;
using FgoPet.CodexAdapter.Relay;

namespace FgoPet.CodexAdapter.Mcp;

public sealed class CodexMcpServer
{
    private readonly ICodexRelaySession _relay;
    private readonly string _sourceType;
    private readonly string _sourceInstance;
    private readonly string _taskId;
    private long _sequence;

    public CodexMcpServer(ICodexRelayConnector connector, string taskId)
        : this(connector, "codex", connector.SourceInstanceId, taskId)
    {
    }

    public CodexMcpServer(ICodexRelaySession relay, string sourceType, string sourceInstance, string taskId)
    {
        _relay = relay ?? throw new ArgumentNullException(nameof(relay));
        _sourceType = sourceType;
        _sourceInstance = sourceInstance;
        _taskId = taskId;
    }

    public async Task<string> HandleAsync(string line, CancellationToken cancellationToken = default)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(line); }
        catch (JsonException) { return Error(default, -32700, "parse_error"); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Error(default, -32600, "invalid_request");
            if (!root.TryGetProperty("id", out var id)) return string.Empty;
            if (id.ValueKind is not JsonValueKind.String and not JsonValueKind.Number and not JsonValueKind.Null)
                return Error(default, -32600, "invalid_request");
            var method = ReadOptionalString(root, "method");
            try
            {
                return method switch
                {
                    "initialize" => Response(id, new { protocolVersion = "2024-11-05", capabilities = new { tools = new { } }, serverInfo = new { name = "fgo-pet-agent", version = "1" } }),
                    "tools/list" => Response(id, new { tools = Tools }),
                    "tools/call" => await HandleToolCallAsync(id, root, cancellationToken).ConfigureAwait(false),
                    _ => Error(id, -32601, "method_not_found"),
                };
            }
            catch (AdapterConnectionException error) { return ConnectionError(id, error.Result); }
            catch (AgentProtocolValidationException) { return Error(id, -32602, "invalid_params"); }
            catch (IOException) { return ConnectionError(id, new(AdapterConnectionStatus.RelayOffline)); }
            catch (InvalidDataException) { return ConnectionError(id, new(AdapterConnectionStatus.RelayOffline)); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return ConnectionError(id, new(AdapterConnectionStatus.RelayOffline));
            }
        }
    }

    private async Task<string> HandleToolCallAsync(JsonElement id, JsonElement root, CancellationToken cancellationToken)
    {
        var parameters = root.TryGetProperty("params", out var value) ? value : default;
        if (parameters.ValueKind != JsonValueKind.Object)
        {
            return Error(id, -32602, "invalid_params");
        }
        var name = ReadOptionalString(parameters, "name");
        var arguments = parameters.TryGetProperty("arguments", out var args) ? args : default;
        var confirmed = arguments.ValueKind == JsonValueKind.Object
            && arguments.TryGetProperty("user_confirmed", out var confirmedValue)
            && confirmedValue.ValueKind == JsonValueKind.True;
        if (!confirmed)
        {
            return Error(id, -32602, "user_confirmation_required");
        }

        if (name is not "report_task_completed" and not "report_goal_completed")
        {
            return Error(id, -32602, "unknown_tool");
        }

        if (_relay is ICodexRelayConnector connector)
        {
            var connection = await connector.EnsureAuthenticatedAsync(cancellationToken).ConfigureAwait(false);
            if (connection.Status != AdapterConnectionStatus.Connected)
            {
                return ConnectionError(id, connection);
            }
        }

        if (name == "report_task_completed")
        {
            var summary = ReadOptionalString(arguments, "summary");
            await SendAsync("task_completed", summary, [], cancellationToken).ConfigureAwait(false);
            return Response(id, new { content = new[] { new { type = "text", text = "ok" } } });
        }

        if (name == "report_goal_completed")
        {
            var covered = arguments.TryGetProperty("covered_task_keys", out var coveredValue)
                && coveredValue.ValueKind == JsonValueKind.Array
                ? coveredValue.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToArray()
                : Array.Empty<string>();
            var prefix = $"{_sourceType}/{_sourceInstance}/";
            if (covered.Length == 0 || covered.Any(key => !key.StartsWith(prefix, StringComparison.Ordinal)))
            {
                return Error(id, -32602, "invalid_goal_coverage");
            }

            await SendAsync("goal_completed", ReadOptionalString(arguments, "summary"), covered, cancellationToken).ConfigureAwait(false);
            return Response(id, new { content = new[] { new { type = "text", text = "ok" } } });
        }

        return Error(id, -32602, "unknown_tool");
    }

    private async Task SendAsync(string eventType, string? summary, IReadOnlyList<string> covered, CancellationToken cancellationToken)
    {
        var message = new AgentEventMessage(
            _sourceType,
            _sourceInstance,
            _taskId,
            Interlocked.Increment(ref _sequence),
            eventType,
            DateTimeOffset.UtcNow,
            Summary: summary,
            CoveredTaskKeys: covered);
        await _relay.SendEventAsync(AgentPayloadSanitizer.Sanitize(message), cancellationToken).ConfigureAwait(false);
    }

    private static string? ReadOptionalString(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var child) && child.ValueKind == JsonValueKind.String
            ? child.GetString()
            : null;

    private static string Response(JsonElement id, object result) => JsonSerializer.Serialize(new { jsonrpc = "2.0", id = ResponseId(id), result });

    private static string Error(JsonElement id, int code, string message) => JsonSerializer.Serialize(new { jsonrpc = "2.0", id = ResponseId(id), error = new { code, message } });

    private static object? ResponseId(JsonElement id) => id.ValueKind == JsonValueKind.Undefined ? null : id;

    private static string ConnectionError(JsonElement id, AdapterConnectionResult connection) => Response(id, new
    {
        isError = true,
        content = new[] { new { type = "text", text = connection.StatusCode } },
        structuredContent = new { status = connection.StatusCode, request_id = connection.RequestId },
    });

    private static object[] Tools =>
    [
        new { name = "report_task_completed", description = "Report a task only after the user confirms delivery.", inputSchema = new { type = "object" } },
        new { name = "report_goal_completed", description = "Report a goal only after the user confirms delivery and coverage.", inputSchema = new { type = "object" } },
    ];
}
