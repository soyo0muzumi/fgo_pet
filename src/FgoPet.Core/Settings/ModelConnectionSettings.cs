using FgoPet.Core.Dialogue;

namespace FgoPet.Core.Settings;

/// <summary>Non-secret model connection metadata. API keys are stored separately.</summary>
public sealed record ModelConnectionSettings
{
    public ModelConnectionSettings(string providerId, string baseUrl, string modelId, bool toolsSupported = true)
    {
        ProviderId = Phase3Validation.Id(providerId, nameof(providerId));
        BaseUrl = Phase3Validation.Text(baseUrl, nameof(baseUrl), 512);
        ModelId = Phase3Validation.Id(modelId, nameof(modelId));
        ToolsSupported = toolsSupported;
    }

    public string ProviderId { get; }
    public string BaseUrl { get; }
    public string ModelId { get; }

    /// <summary>
    /// Per-connection memory of whether the service accepts the tools parameter.
    /// A rejected request (e.g. HTTP 400 "unknown parameter") flips this to false so
    /// later turns skip tools without retrying; changing the endpoint resets the flag.
    /// </summary>
    public bool ToolsSupported { get; init; } = true;
}
