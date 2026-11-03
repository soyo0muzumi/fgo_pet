using System.Text.Json;
using FgoPet.Extensibility;

namespace FgoPet.Plugin.SocialSources;

/// <summary>
/// Host-owned grant for exactly one social platform. The platform name is stored in the file so a
/// consent written for one service can never authorize another, mirroring web.search's provider rule.
/// </summary>
public sealed record SocialSourceSettings(bool Enabled, string AuthorizationId, long AuthorizationVersion,
    string Platform)
{
    public const string Bilibili = "bilibili";
    public const string Xiaohongshu = "xiaohongshu";
    public const string Xiaoheihe = "xiaoheihe";

    public static readonly string[] Known = [Bilibili, Xiaohongshu, Xiaoheihe];

    public static bool IsKnown(string? platform) => platform is not null
        && Array.IndexOf(Known, platform) >= 0;

    /// <summary>Read-only inspection. Never creates the file, repairs it or touches the network.</summary>
    public static SocialSourceSettingsReadResult Read(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length > 4096) return new(null, "SOCIAL_CONFIG_INVALID");
            using var document = JsonDocument.Parse(file, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new(null, "SOCIAL_CONFIG_INVALID");
            var names = root.EnumerateObject().Select(property => property.Name).ToArray();
            if (names.Distinct(StringComparer.Ordinal).Count() != names.Length
                || names.Except([nameof(Enabled), nameof(AuthorizationId), nameof(AuthorizationVersion),
                    nameof(Platform)], StringComparer.Ordinal).Any())
                return new(null, "SOCIAL_CONFIG_INVALID");
            var settings = root.Deserialize<SocialSourceSettings>();
            if (!ValidGrant(settings)) return new(null, "SOCIAL_CONFIG_INVALID");
            return new(settings, settings!.Enabled ? null : "SOCIAL_NOT_ENABLED");
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        { return new(null, "SOCIAL_NOT_CONFIGURED"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return new(null, "SOCIAL_CONFIG_UNREADABLE"); }
        catch (Exception error) when (error is JsonException or ArgumentException or NotSupportedException)
        { return new(null, "SOCIAL_CONFIG_INVALID"); }
    }

    private static bool ValidGrant(SocialSourceSettings? settings) => settings is { AuthorizationVersion: > 0 }
        && IsKnown(settings.Platform)
        && Guid.TryParseExact(settings.AuthorizationId, "N", out var id) && id != Guid.Empty;

    internal static bool IsAuthorized(SocialSourceSettings? settings, string platform) => ValidGrant(settings)
        && settings is { Enabled: true } && string.Equals(settings.Platform, platform, StringComparison.Ordinal);
}

public sealed record SocialSourceSettingsReadResult(SocialSourceSettings? Settings, string? ErrorCode);

public sealed record SocialSourceSettingsSnapshot(bool Configured, bool Enabled, bool Available, string? ErrorCode);

/// <summary>One settings file per platform. Inspection never creates files, repairs state or reads the network.</summary>
public sealed class SocialSourceSettingsStore(string directory, string platform)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string Path => System.IO.Path.Combine(directory, "social-" + platform + ".json");

    public SocialSourceSettings? Load() => SocialSourceSettings.Read(Path) is { ErrorCode: null, Settings: { } settings }
        ? settings : null;

    public SocialSourceSettingsSnapshot GetSnapshot()
    {
        var read = SocialSourceSettings.Read(Path);
        var code = read.ErrorCode;
        // A grant written for a different platform is not a valid grant for this one.
        if (read.Settings is not null && !string.Equals(read.Settings.Platform, platform, StringComparison.Ordinal))
            code = "SOCIAL_CONFIG_INVALID";
        return new(read.Settings is not null, code is null, code is null, code);
    }

    public ToolAvailability GetAvailability()
    {
        var snapshot = GetSnapshot();
        return new(snapshot.Available, snapshot.Available ? null : snapshot.ErrorCode ?? "SOCIAL_NOT_CONFIGURED");
    }

    /// <summary>Only an explicit settings action writes a grant. Every write rotates the identifier.</summary>
    public async Task<SocialSourceSettingsSnapshot> SetEnabledAsync(bool enabled, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        string? temporary = null;
        try
        {
            token.ThrowIfCancellationRequested();
            var read = SocialSourceSettings.Read(Path);
            if (read.ErrorCode == "SOCIAL_CONFIG_UNREADABLE") return GetSnapshot();
            var current = read.Settings;
            var revision = current is { AuthorizationVersion: < long.MaxValue } ? current.AuthorizationVersion + 1 : 1;
            var settings = new SocialSourceSettings(enabled, Guid.NewGuid().ToString("N"), revision, platform);
            Directory.CreateDirectory(directory);
            temporary = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var bytes = JsonSerializer.SerializeToUtf8Bytes(settings, new JsonSerializerOptions { WriteIndented = true });
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await output.WriteAsync(bytes, token);
                await output.FlushAsync(token);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, Path, overwrite: true);
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

/// <summary>Metadata-only diagnostics; excludes queries, URLs, page bodies, credentials and paths.</summary>
public sealed record SocialSourceDiagnostic(DateTimeOffset Timestamp, string Correlation, string Stage, string? ErrorCode);
