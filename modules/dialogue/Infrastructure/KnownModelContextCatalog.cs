using FgoPet.Core.Dialogue;
using FgoPet.Core.Settings;

namespace FgoPet.Infrastructure.Providers;

/// <summary>Versioned, endpoint-specific metadata; never inferred from a model's name.</summary>
public static class KnownModelContextCatalog
{
    public static ModelContextLimit? Find(ModelConnectionSettings settings)
    {
        if (!Uri.TryCreate(settings.BaseUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != "https" || !uri.IsDefaultPort || uri.Query.Length != 0 || uri.UserInfo.Length != 0)
            return null;
        var path = uri.AbsolutePath.TrimEnd('/');
        var route = ModelRouteKey.From(settings);
        if (settings.ProviderId == "openai" && uri.Host == "api.openai.com" && path == "/v1" &&
            settings.ModelId is "gpt-4.1-mini" or "gpt-4.1-mini-2025-04-14")
            return new(route, 1_047_576, 32_768, ContextLimitSource.KnownModel,
                "2026-09-23 https://developers.openai.com/api/docs/models/gpt-4.1-mini");
        if (settings.ProviderId == "deepseek" && uri.Host == "api.deepseek.com" && path is "" or "/v1" &&
            settings.ModelId is "deepseek-flash" or "deepseek-v4-pro")
            return new(route, 1_000_000, 384_000, ContextLimitSource.KnownModel,
                "2026-09-23 https://api-docs.deepseek.com/quick_start/pricing/ (conservative decimal M/K)");
        return null;
    }
}
