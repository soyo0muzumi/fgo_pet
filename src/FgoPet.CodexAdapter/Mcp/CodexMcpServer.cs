using System.Text.Json;
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

    public CodexMcpServer(ICodexRelaySession relay, string sourceType, string sourceInstance, string taskId)
    {
        _relay = relay ?? throw new ArgumentNullException(nameof(relay));
        _sourceType = sourceType;
        _sourceInstance = sourceInstance;
        _taskId = taskId;
    }

    public async Task<string> HandleAsync(string line, CancellationToken cancellationToken = default)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        var id = root.TryGetProperty("id", out var idValue) ? idValue.Clone() : default;
        var method = root.TryGetProperty("method", out var methodValue) ? methodValue.GetString() : null;
        return method switch
        {
            "initialize" => Response(id, new { protocolVersion = "2024-11-05", capabilities = new { tools = new { } }, serverInfo = new { name = "fgo-pet-agent", version = "1" } }),
            "notifications/initialized" => Response(id, new { }),
            "tools/list" => Response(id, new { tools = Tools }),
            "tools/call" => await HandleToolCallAsync(id, root, cancellationToken).ConfigureAwait(false),
            _ => Error(id, -32601, "method_not_found"),
        };
    }

    private async Task<string> HandleToolCallAsync(JsonElement id, JsonElement root, CancellationToken cancellationToken)
    {
        var parameters = root.TryGetProperty("params", out var value) ? value : default;
        var name = parameters.TryGetProperty("name", out var nameValue) ? nameValue.GetString() : null;
        var arguments = parameters.TryGetProperty("arguments", out var args) ? args : default;
        var confirmed = arguments.ValueKind == JsonValueKind.Object
            && arguments.TryGetProperty("user_confirmed", out var confirmedValue)
            && confirmedValue.ValueKind == JsonValueKind.True;
        if (!confirmed)
        {
            return Error(id, -32602, "user_confirmation_required");
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

    private static string Response(JsonElement id, object result) => JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result });

    private static string Error(JsonElement id, int code, string message) => JsonSerializer.Serialize(new { jsonrpc = "2.0", id, error = new { code, message } });

    private static object[] Tools =>
    [
        new { name = "report_task_completed", description = "Report a task only after the user confirms delivery.", inputSchema = new { type = "object" } },
        new { name = "report_goal_completed", description = "Report a goal only after the user confirms delivery and coverage.", inputSchema = new { type = "object" } },
    ];
}
