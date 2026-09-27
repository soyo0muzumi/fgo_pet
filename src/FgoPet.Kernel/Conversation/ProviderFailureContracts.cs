using System.Net;

namespace FgoPet.Infrastructure.Providers;

public enum ProviderFailureCategory
{
    Configuration,
    Authentication,
    RateLimited,
    Network,
    ServiceUnavailable,
    InvalidResponse,
    ToolsRejected,
    ContextLimitExceeded,
}

public sealed class ProviderRequestException : Exception
{
    public ProviderRequestException(
        ProviderFailureCategory category,
        string message,
        Exception? innerException = null,
        HttpStatusCode? httpStatusCode = null,
        string? providerCode = null,
        bool requestWasSent = false)
        : base(message, innerException)
    {
        Category = category;
        HttpStatusCode = httpStatusCode;
        ProviderCode = providerCode;
        RequestWasSent = requestWasSent;
    }

    public ProviderFailureCategory Category { get; }
    public HttpStatusCode? HttpStatusCode { get; }
    public string? ProviderCode { get; }
    public bool RequestWasSent { get; }
}
