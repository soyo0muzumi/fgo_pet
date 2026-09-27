using System.Security.Cryptography;
using System.Text;
using FgoPet.Core.Dialogue;

namespace FgoPet.Infrastructure.Providers;

/// <summary>Conservative text estimate with exact-envelope usage anchors. Never treats chars/4 as a CJK guarantee.</summary>
public sealed class RequestTokenMeter : IRequestTokenMeter
{
    private readonly object _gate = new();
    private readonly Dictionary<(ModelRouteKey Route, string Fingerprint), ChatUsage> _anchors = new();

    public TokenMeasurement Measure(ModelRouteKey route, ChatRequest request)
    {
        var fingerprint = Fingerprint(route, request);
        lock (_gate)
            if (_anchors.TryGetValue((route, fingerprint), out var usage))
                return new(usage.InputTokens, TokenCountKind.ProviderUsage, fingerprint);
        try
        {
            var bytes = Encoding.UTF8.GetByteCount(ChatRequestInputEnvelope.Write(route.ModelId, request));
            return new(checked(bytes + 12 * request.Messages.Count + 32), TokenCountKind.Estimated, fingerprint);
        }
        catch (OverflowException) { throw new PromptBudgetException(PromptBudgetFailure.InsufficientContext); }
    }

    public void RecordUsage(ModelRouteKey route, ChatRequest request, ChatUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        lock (_gate)
        {
            if (_anchors.Count >= 64) _anchors.Clear();
            _anchors[(route, Fingerprint(route, request))] = usage;
        }
    }

    public void Invalidate(ModelRouteKey route)
    {
        lock (_gate)
            foreach (var key in _anchors.Keys.Where(key => key.Route == route).ToArray()) _anchors.Remove(key);
    }

    private static string Fingerprint(ModelRouteKey route, ChatRequest request) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            System.Text.Json.JsonSerializer.Serialize(new
            {
                route,
                input = ChatRequestInputEnvelope.Write(route.ModelId, request),
                request.MaxOutputTokens,
                metadata = request.Metadata.OrderBy(item => item.Key, StringComparer.Ordinal)
            }))));
}

/// <summary>The deterministic input envelope measured before a provider adds transport options.</summary>
public static class ChatRequestInputEnvelope
{
    private static readonly System.Text.Json.JsonSerializerOptions Options = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    public static string Write(string modelId, ChatRequest request) =>
        System.Text.Json.JsonSerializer.Serialize(BuildPayload(modelId, request), Options);
    public static Dictionary<string, object?> BuildPayload(string modelId, ChatRequest request)
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
