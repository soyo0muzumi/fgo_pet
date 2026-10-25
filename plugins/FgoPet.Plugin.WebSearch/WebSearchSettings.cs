using System.Text.Json;

namespace FgoPet.Plugin.WebSearch;

/// <summary>Host-owned search grant. Contains neither a credential nor a configurable endpoint.</summary>
public sealed record WebSearchSettings(bool Enabled, string AuthorizationId, long AuthorizationVersion)
{
    public const string CredentialTarget = "FgoPet:WebSearch:Tavily";

    public static WebSearchSettings? Load(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length > 4096) return null;
            using var document = JsonDocument.Parse(file, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var names = root.EnumerateObject().Select(property => property.Name).ToArray();
            if (names.Length != 3 || names.Distinct(StringComparer.Ordinal).Count() != 3
                || names.Except([nameof(Enabled), nameof(AuthorizationId), nameof(AuthorizationVersion)], StringComparer.Ordinal).Any())
                return null;
            var settings = root.Deserialize<WebSearchSettings>();
            return IsAuthorized(settings) ? settings : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { return null; }
    }

    internal static bool IsAuthorized(WebSearchSettings? settings) => settings is { Enabled: true, AuthorizationVersion: > 0 }
        && Guid.TryParseExact(settings.AuthorizationId, "N", out var id) && id != Guid.Empty;
}

/// <summary>Metadata-only diagnostics; excludes queries, URLs, content, credentials and upstream bodies.</summary>
public sealed record WebSearchDiagnostic(DateTimeOffset Timestamp, string Correlation, string Stage, string? ErrorCode);
