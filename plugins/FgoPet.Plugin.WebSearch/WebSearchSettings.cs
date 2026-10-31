using System.Text.Json;
using FgoPet.Extensibility;

namespace FgoPet.Plugin.WebSearch;

/// <summary>Host-owned grant. Provider is explicit so an old Tavily grant cannot authorize a different service.</summary>
public sealed record WebSearchSettings(bool Enabled, string AuthorizationId, long AuthorizationVersion,
    string Provider = "duckduckgo")
{
    public static WebSearchSettings? Load(string path)
    {
        var read = Read(path);
        return read.ErrorCode is null ? read.Settings : null;
    }

    public static WebSearchSettingsReadResult Read(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length > 4096) return new(null, "WEB_SEARCH_CONFIG_INVALID");
            using var document = JsonDocument.Parse(file, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new(null, "WEB_SEARCH_CONFIG_INVALID");
            var names = root.EnumerateObject().Select(property => property.Name).ToArray();
            if (names.Distinct(StringComparer.Ordinal).Count() != names.Length
                || names.Except([nameof(Enabled), nameof(AuthorizationId), nameof(AuthorizationVersion), nameof(Provider)], StringComparer.Ordinal).Any())
                return new(null, "WEB_SEARCH_CONFIG_INVALID");
            if (names.Length == 3 && !root.TryGetProperty(nameof(Provider), out _))
                return new(null, "WEB_SEARCH_PROVIDER_CHANGED");
            if (names.Length != 4) return new(null, "WEB_SEARCH_CONFIG_INVALID");
            var settings = root.Deserialize<WebSearchSettings>();
            if (!ValidGrant(settings)) return new(null, "WEB_SEARCH_CONFIG_INVALID");
            if (settings!.Provider != "duckduckgo") return new(null, "WEB_SEARCH_PROVIDER_CHANGED");
            return new(settings, settings.Enabled ? null : "WEB_SEARCH_NOT_ENABLED");
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        { return new(null, "WEB_SEARCH_NOT_CONFIGURED"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return new(null, "WEB_SEARCH_CONFIG_UNREADABLE"); }
        catch (Exception error) when (error is JsonException or ArgumentException or NotSupportedException)
        { return new(null, "WEB_SEARCH_CONFIG_INVALID"); }
    }

    private static bool ValidGrant(WebSearchSettings? settings) => settings is { AuthorizationVersion: > 0 }
        && Guid.TryParseExact(settings.AuthorizationId, "N", out var id) && id != Guid.Empty;

    internal static bool IsAuthorized(WebSearchSettings? settings) => ValidGrant(settings)
        && settings is { Enabled: true, Provider: "duckduckgo" };
}

public sealed record WebSearchSettingsReadResult(WebSearchSettings? Settings, string? ErrorCode);
public sealed record WebSearchSettingsSnapshot(bool Configured, bool Enabled, bool Available, string? ErrorCode);

/// <summary>Only explicit settings commands write a grant. Inspection never creates or repairs configuration.</summary>
public sealed class WebSearchSettingsStore(string path, string workerPath)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public WebSearchSettings? Load() => WebSearchSettings.Load(path);

    public WebSearchSettingsSnapshot GetSnapshot()
    {
        var read = WebSearchSettings.Read(path);
        var code = read.ErrorCode;
        if (code is null && !File.Exists(workerPath)) code = "WEB_SEARCH_RUNTIME_MISSING";
        return new(read.Settings is not null, read.Settings?.Enabled == true, code is null, code);
    }

    public ToolAvailability GetAvailability()
    {
        var snapshot = GetSnapshot();
        return new(snapshot.Available, snapshot.ErrorCode);
    }

    public async Task<WebSearchSettingsSnapshot> SetEnabledAsync(bool enabled, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        string? temporary = null;
        try
        {
            token.ThrowIfCancellationRequested();
            var read = WebSearchSettings.Read(path);
            if (read.ErrorCode == "WEB_SEARCH_CONFIG_UNREADABLE") return GetSnapshot();
            var current = read.Settings;
            var revision = current is { AuthorizationVersion: < long.MaxValue } ? current.AuthorizationVersion + 1 : 1;
            var settings = new WebSearchSettings(enabled, Guid.NewGuid().ToString("N"), revision);
            var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
            Directory.CreateDirectory(directory);
            temporary = Path.Combine(directory, ".web-search-" + Guid.NewGuid().ToString("N") + ".tmp");
            var bytes = JsonSerializer.SerializeToUtf8Bytes(settings, new JsonSerializerOptions { WriteIndented = true });
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await output.WriteAsync(bytes, token);
                await output.FlushAsync(token);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
            return GetSnapshot();
        }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
            _gate.Release();
        }
    }
}

/// <summary>Metadata-only diagnostics; excludes queries, URLs, content, credentials and upstream bodies.</summary>
public sealed record WebSearchDiagnostic(DateTimeOffset Timestamp, string Correlation, string Stage, string? ErrorCode);
