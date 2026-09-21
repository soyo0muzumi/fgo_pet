using FgoPet.Core.Dialogue;

namespace FgoPet.Infrastructure.Providers;

public sealed class ProviderCatalog
{
    private static readonly IReadOnlyList<ProviderDescriptor> Definitions =
    [
        new("openai", "OpenAI", "https://api.openai.com/v1"),
        new("deepseek", "DeepSeek", "https://api.deepseek.com/v1"),
        new("custom-openai-compatible", "自定义 OpenAI-compatible", "http://127.0.0.1:1234/v1"),
    ];

    private readonly IReadOnlyDictionary<string, ProviderDescriptor> _byId =
        Definitions.ToDictionary(item => item.ProviderId, StringComparer.Ordinal);

    public IReadOnlyList<ProviderDescriptor> Providers => Definitions;

    public ProviderDescriptor Get(string providerId) =>
        _byId.TryGetValue(providerId, out var descriptor)
            ? descriptor
            : throw new KeyNotFoundException($"Unknown provider '{providerId}'.");
}
