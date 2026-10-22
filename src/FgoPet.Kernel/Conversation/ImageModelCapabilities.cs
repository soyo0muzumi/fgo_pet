using FgoPet.Core.Settings;

namespace FgoPet.Core.Dialogue;

/// <summary>Closed, documented capability allow-list, not a name prefix or custom endpoint guess.</summary>
public static class ImageModelCapabilities
{
    // https://developers.openai.com/api/docs/models/gpt-4.1-mini
    // The official model documents image input and Chat Completions. Unknown routes fail closed.
    private static readonly string OfficialOpenAiEndpoint = ModelRouteKey.From(
        new ModelConnectionSettings("openai", "https://api.openai.com/v1", "fixture")).EndpointKey;
    // https://api-docs.deepseek.com/guides/vision/
    // https://api-docs.deepseek.com/quick_start/agent_integrations/workbuddy/ documents /v1 as well.
    // deepseek-flash is the API alias for DeepSeek-V4.1-Flash-1017, not a family-name prefix.
    private static readonly HashSet<string> OfficialDeepSeekEndpoints = new(StringComparer.Ordinal)
    {
        ModelRouteKey.From(new("deepseek", "https://api.deepseek.com", "fixture")).EndpointKey,
        ModelRouteKey.From(new("deepseek", "https://api.deepseek.com/v1", "fixture")).EndpointKey,
    };
    // Explicitly supported local subscription contract. This identifies a route;
    // it does not authenticate the service or replace its existing credentials.
    private static readonly string LocalBuddyEndpoint = ModelRouteKey.From(
        new("custom-openai-compatible", "http://127.0.0.1:8326/v1", "fixture")).EndpointKey;

    public static bool IsLocalBuddySubscription(ModelRouteKey route) =>
        route.ProviderId == "custom-openai-compatible" && route.EndpointKey == LocalBuddyEndpoint
            && route.ModelId == "buddy/deepseek-v4.1-flash";

    public static bool Supports(ModelRouteKey route) =>
        route.ProviderId == "openai" && route.EndpointKey == OfficialOpenAiEndpoint
            && route.ModelId is "gpt-4.1-mini" or "gpt-4.1-mini-2025-04-14"
        || route.ProviderId == "deepseek" && OfficialDeepSeekEndpoints.Contains(route.EndpointKey)
            && route.ModelId == "deepseek-flash"
        || IsLocalBuddySubscription(route);
}
