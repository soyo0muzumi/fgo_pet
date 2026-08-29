namespace FgoPet.Core.Dialogue;

public sealed record ProviderModel
{
    public ProviderModel(string id, string? displayName = null)
    {
        Id = Phase3Validation.Id(id, nameof(id));
        DisplayName = string.IsNullOrWhiteSpace(displayName)
            ? Id
            : Phase3Validation.Text(displayName, nameof(displayName), 128);
    }

    public string Id { get; }
    public string DisplayName { get; }
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
