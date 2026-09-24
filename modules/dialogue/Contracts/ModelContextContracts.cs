using System.Security.Cryptography;
using System.Text;
using FgoPet.Core.Settings;

namespace FgoPet.Core.Dialogue;

public enum ContextLimitSource { Override, ProviderMetadata, KnownModel, ConservativeFallback }

public sealed record ModelRouteKey(string ProviderId, string EndpointKey, string ModelId, string ConfigurationRevision)
{
    public static ModelRouteKey From(ModelConnectionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var uri = new Uri(settings.BaseUrl, UriKind.Absolute);
        var builder = new UriBuilder(uri) { Fragment = string.Empty, Path = uri.AbsolutePath.TrimEnd('/') + "/" };
        var endpoint = Hash(builder.Uri.AbsoluteUri);
        var configuration = Hash($"{settings.ToolsSupported}:{settings.ContextWindowOverride}:{settings.MaxOutputTokens}");
        return new(settings.ProviderId, endpoint, settings.ModelId, configuration);
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}

public sealed record ModelContextLimit(ModelRouteKey Route, int WindowTokens, int? MaxOutputTokens,
    ContextLimitSource Source, string SourceRevision)
{
    public string DisplayText => Source switch
    {
        ContextLimitSource.Override => $"上下文上限 {WindowTokens:N0} tokens · 手工设置",
        ContextLimitSource.ProviderMetadata => $"上下文上限 {WindowTokens:N0} tokens · 服务提供方",
        ContextLimitSource.KnownModel => $"上下文上限 {WindowTokens:N0} tokens · 已知模型资料",
        _ => $"未获取到上限，暂按 {WindowTokens:N0} tokens 保守估算",
    };
}

public interface IModelContextResolver
{
    Task<ModelContextLimit> ResolveAsync(ModelConnectionSettings settings, CancellationToken cancellationToken);
}
