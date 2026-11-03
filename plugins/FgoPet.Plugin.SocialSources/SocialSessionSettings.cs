using System.Text.Json;
using FgoPet.Extensibility;

namespace FgoPet.Plugin.SocialSources;

/// <summary>
/// Host-owned grant for the optional authenticated session of one platform. It never contains the
/// session secret: that lives in Windows Credential Manager under <see cref="CredentialTarget"/>, so a
/// copied settings file grants no access, and revoking means rotating this grant or deleting the secret.
/// </summary>
public sealed record SocialSessionSettings(bool Enabled, string AuthorizationId, long AuthorizationVersion,
    string Platform)
{
    /// <summary>Resolved per platform; never derived from model input.</summary>
    public static string CredentialTarget(string platform) => "FgoPet.SocialSources." + platform;

    public static bool IsKnown(string? platform) => SocialSourceSettings.IsKnown(platform);

    public static SocialSessionSettingsReadResult Read(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length > 4096) return new(null, "SOCIAL_SESSION_CONFIG_INVALID");
            using var document = JsonDocument.Parse(file, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new(null, "SOCIAL_SESSION_CONFIG_INVALID");
            var names = root.EnumerateObject().Select(property => property.Name).ToArray();
            if (names.Distinct(StringComparer.Ordinal).Count() != names.Length
                || names.Except([nameof(Enabled), nameof(AuthorizationId), nameof(AuthorizationVersion),
                    nameof(Platform)], StringComparer.Ordinal).Any())
                return new(null, "SOCIAL_SESSION_CONFIG_INVALID");
            var settings = root.Deserialize<SocialSessionSettings>();
            if (settings is not { AuthorizationVersion: > 0 } || !IsKnown(settings.Platform)
                || !Guid.TryParseExact(settings.AuthorizationId, "N", out var id) || id == Guid.Empty)
                return new(null, "SOCIAL_SESSION_CONFIG_INVALID");
            if (!string.Equals(settings.Platform, PlatformOf(path), StringComparison.Ordinal))
                return new(null, "SOCIAL_SESSION_CONFIG_INVALID");
            return new(settings, settings.Enabled ? null : "SOCIAL_SESSION_NOT_ENABLED");
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        { return new(null, "SOCIAL_SESSION_NOT_CONFIGURED"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return new(null, "SOCIAL_SESSION_CONFIG_UNREADABLE"); }
        catch (Exception error) when (error is JsonException or ArgumentException or NotSupportedException)
        { return new(null, "SOCIAL_SESSION_CONFIG_INVALID"); }
    }

    internal static string PlatformOf(string path)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        return name.StartsWith("social-session-", StringComparison.Ordinal)
            ? name["social-session-".Length..] : string.Empty;
    }

    internal static bool IsAuthorized(SocialSessionSettings? settings, string platform) =>
        settings is { Enabled: true, AuthorizationVersion: > 0 }
        && string.Equals(settings.Platform, platform, StringComparison.Ordinal)
        && Guid.TryParseExact(settings.AuthorizationId, "N", out var id) && id != Guid.Empty;
}

public sealed record SocialSessionSettingsReadResult(SocialSessionSettings? Settings, string? ErrorCode);

/// <summary>
/// Local session state. <c>Connected</c> means a live call has succeeded, never just "configured".
/// Whether the secret exists is deliberately not reported here: readiness is a hint, and a missing
/// secret surfaces as a distinct execution error rather than a configuration guess.
/// </summary>
public sealed record SocialSessionSnapshot(bool Configured, bool Enabled, bool Connected, string? ErrorCode);

/// <summary>Per-platform session grant. Inspection reads no secret and performs no network I/O.</summary>
public sealed class SocialSessionSettingsStore(string directory, string platform)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string Path => System.IO.Path.Combine(directory, "social-session-" + platform + ".json");
    public string CredentialTarget => SocialSessionSettings.CredentialTarget(platform);

    public SocialSessionSettings? Load() => SocialSessionSettings.Read(Path) is
        { ErrorCode: null, Settings: { } settings } ? settings : null;

    /// <summary>Reads the grant only. Secret existence is reported separately by the connection owner.</summary>
    public SocialSessionSettingsReadResult Read() => SocialSessionSettings.Read(Path);

    public async Task<SocialSessionSnapshot> SetEnabledAsync(bool enabled, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        string? temporary = null;
        try
        {
            token.ThrowIfCancellationRequested();
            var read = SocialSessionSettings.Read(Path);
            if (read.ErrorCode == "SOCIAL_SESSION_CONFIG_UNREADABLE")
                return new(read.Settings is not null, read.Settings?.Enabled == true, false,
                    "SOCIAL_SESSION_CONFIG_UNREADABLE");
            var revision = read.Settings is { AuthorizationVersion: < long.MaxValue }
                ? read.Settings.AuthorizationVersion + 1 : 1;
            var settings = new SocialSessionSettings(enabled, Guid.NewGuid().ToString("N"), revision, platform);
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
            return new(true, enabled, false, enabled ? null : "SOCIAL_SESSION_NOT_ENABLED");
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
