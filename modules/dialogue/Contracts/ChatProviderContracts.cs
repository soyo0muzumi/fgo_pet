using FgoPet.Core.Validation;

namespace FgoPet.Core.Dialogue;

public sealed record ProviderModel
{
    public ProviderModel(string id, string? displayName = null, int? contextWindowTokens = null, int? maxOutputTokens = null)
    {
        Id = Phase3Validation.Id(id, nameof(id));
        if (contextWindowTokens is <= 0 || maxOutputTokens is <= 0)
            throw new ArgumentOutOfRangeException(nameof(contextWindowTokens));
        ContextWindowTokens = contextWindowTokens;
        MaxOutputTokens = maxOutputTokens;
        DisplayName = string.IsNullOrWhiteSpace(displayName)
            ? Id
            : Phase3Validation.Text(displayName, nameof(displayName), 128);
    }

    public string Id { get; }
    public string DisplayName { get; }
    public int? ContextWindowTokens { get; }
    public int? MaxOutputTokens { get; }
}

public sealed record ProviderDescriptor
{
    public ProviderDescriptor(string providerId, string displayName, string defaultBaseUrl)
    {
        ProviderId = Phase3Validation.Id(providerId, nameof(providerId));
        DisplayName = Phase3Validation.Text(displayName, nameof(displayName), 128);
        DefaultBaseUrl = Phase3Validation.Text(defaultBaseUrl, nameof(defaultBaseUrl), 512);
    }

    public string ProviderId { get; }
    public string DisplayName { get; }
    public string DefaultBaseUrl { get; }
}

public interface IChatProvider
{
    string ProviderId { get; }
    string ModelId { get; }

    IAsyncEnumerable<ChatStreamChunk> StreamAsync(ChatRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken cancellationToken);
}
