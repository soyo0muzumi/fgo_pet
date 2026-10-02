using System.Text;
using System.Text.Json;

namespace FgoPet.UiSdk;

public sealed record WebSurfaceMessage(string Type, string? RequestId, JsonElement Payload);

/// <summary>Validates a local Web surface's navigation and narrow message envelope.</summary>
public sealed class WebSurfacePolicy
{
    public const int MaxMessageBytes = 65_536;
    private readonly Uri _document;
    private readonly HashSet<string> _commands;

    public WebSurfacePolicy(string documentUrl, IEnumerable<string> commands)
    {
        if (!Uri.TryCreate(documentUrl, UriKind.Absolute, out var document)
            || document.Scheme != Uri.UriSchemeHttps || document.IsDefaultPort is false
            || !string.IsNullOrEmpty(document.UserInfo) || !string.IsNullOrEmpty(document.Fragment))
            throw new ArgumentException("WEB_INVALID_DOCUMENT_URL", nameof(documentUrl));
        _document = document;
        _commands = new HashSet<string>(commands ?? throw new ArgumentNullException(nameof(commands)), StringComparer.Ordinal);
    }

    public string DocumentUrl => _document.AbsoluteUri;
    public string DocumentHost => _document.IdnHost;

    public bool AllowsNavigation(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var candidate)
        && candidate.Scheme == Uri.UriSchemeHttps
        && candidate.IsDefaultPort
        && string.IsNullOrEmpty(candidate.UserInfo)
        && string.Equals(candidate.IdnHost, _document.IdnHost, StringComparison.OrdinalIgnoreCase);

    public WebSurfaceMessage? ValidateMessage(string? messageSource, string? currentSource, string? json)
    {
        if (!string.Equals(messageSource, DocumentUrl, StringComparison.Ordinal)
            || !string.Equals(currentSource, DocumentUrl, StringComparison.Ordinal)
            || string.IsNullOrEmpty(json) || Encoding.UTF8.GetByteCount(json) > MaxMessageBytes)
            return null;

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var typeElement)
                || typeElement.ValueKind != JsonValueKind.String)
                return null;
            var type = typeElement.GetString();
            if (type is null || type is not ("ready" or "theme.ack") && !_commands.Contains(type))
                return null;
            if (type is "ready" or "theme.ack") return new(type, null, root.Clone());

            if (!root.TryGetProperty("requestId", out var idElement)
                || idElement.ValueKind != JsonValueKind.String
                || idElement.GetString() is not { Length: > 0 and <= 64 } id
                || !id.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
                || !root.TryGetProperty("payload", out var payload)
                || payload.ValueKind != JsonValueKind.Object)
                return null;
            return new(type, id, payload.Clone());
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
