using System.Text.Encodings.Web;
using System.Text.Json;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Settings;

namespace FgoPet.Infrastructure.Providers;

/// <summary>One deterministic projection for the wire body and its token/fingerprint accounting.</summary>
public static class ChatRequestPayloadWriter
{
    private static readonly JsonSerializerOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly string OfficialOpenAiEndpoint = ModelRouteKey.From(
        new ModelConnectionSettings("openai", "https://api.openai.com/v1", "fixture")).EndpointKey;
    private static readonly HashSet<string> OfficialDeepSeekEndpoints = new(StringComparer.Ordinal)
    {
        ModelRouteKey.From(new("deepseek", "https://api.deepseek.com", "fixture")).EndpointKey,
        ModelRouteKey.From(new("deepseek", "https://api.deepseek.com/v1", "fixture")).EndpointKey,
    };

    public static string Write(ModelRouteKey route, ChatRequest request)
    {
        var officialOpenAi = route.ProviderId == "openai" && route.EndpointKey == OfficialOpenAiEndpoint;
        var payload = Input(route.ModelId, request);
        payload["stream"] = true;
        if (request.MaxOutputTokens is int output)
            payload[officialOpenAi ? "max_completion_tokens" : "max_tokens"] = output;
        if (officialOpenAi) payload["stream_options"] = new { include_usage = true };
        // DeepSeek defaults to thinking, which can exhaust a bounded auxiliary response
        // before any JSON/summary content is emitted. Primary chats retain provider defaults.
        // Only application-created metadata opts in; never infer purpose from prompt text.
        if (route.ProviderId == "deepseek" && OfficialDeepSeekEndpoints.Contains(route.EndpointKey) &&
            route.ModelId is "deepseek-flash" or "deepseek-v4-pro" &&
            request.Metadata.TryGetValue("fgo_auxiliary", out var purpose) &&
            purpose is "history_recall" or "context_summary" or "memory_extraction")
        {
            payload["thinking"] = new { type = "disabled" };
            if (purpose is "history_recall" or "memory_extraction")
                payload["response_format"] = new { type = "json_object" };
        }
        return JsonSerializer.Serialize(payload, Options);
    }

    public static string WriteInput(string modelId, ChatRequest request) => JsonSerializer.Serialize(Input(modelId, request), Options);

    private static Dictionary<string, object?> Input(string modelId, ChatRequest request)
    {
        var payload = new Dictionary<string, object?> {
            ["model"] = modelId,
            ["messages"] = request.Messages.Select(message => new {
                role = message.Role.ToString().ToLowerInvariant(), content = message.Text
            }),
        };
        if (request.Tools is { Count: > 0 })
        {
            payload["tools"] = request.Tools.Select(tool => new {
                type = tool.Type, function = new { name = tool.Name, description = tool.Description, parameters = tool.Parameters }
            });
            payload["tool_choice"] = request.ToolChoice ?? "auto";
        }
        return payload;
    }
}
