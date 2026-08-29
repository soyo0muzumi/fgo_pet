using FgoPet.Core.Dialogue;

namespace FgoPet.Core.Settings;

/// <summary>Non-secret model connection metadata. API keys are stored separately.</summary>
public sealed record ModelConnectionSettings
{
    public ModelConnectionSettings(string providerId, string baseUrl, string modelId)
    {
        ProviderId = Phase3Validation.Id(providerId, nameof(providerId));
        BaseUrl = Phase3Validation.Text(baseUrl, nameof(baseUrl), 512);
        ModelId = Phase3Validation.Id(modelId, nameof(modelId));
    }

    public string ProviderId { get; }
    public string BaseUrl { get; }
    public string ModelId { get; }
}
