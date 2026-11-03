using System.Text.Json;
using System.Security.Cryptography;
using System.Collections.Concurrent;

namespace FgoPet.Plugin.Browser;

/// <summary>Local host grant. Never supplied by a model, page, package or tool argument.</summary>
public sealed record ChromeBrowserSettings(bool Enabled, string NodePath, string ServerPath, string? UserDataDirectory = null,
    bool VisualEnabled = false, string? VisualGrantId = null, string? AuthorizationId = null)
{
    public const string ServerVersion = "1.10.1";
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> WriteGates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    // Browser setup and legacy visual-grant writes share the same file writer gate.
    internal static SemaphoreSlim GetWriteGate(string path) => WriteGates.GetOrAdd(Path.GetFullPath(path), _ => new(1, 1));
    public static ChromeBrowserSettings? Load(string path) => Read(path, inspectDependencies: false) is
        { Settings: { Enabled: true } settings, ErrorCode: null } ? settings : null;

    internal static ChromeBrowserSetupState Read(string path, bool inspectDependencies = true)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length > 8192) return new(null, "BROWSER_SETTINGS_INVALID");
            var bytes = new byte[(int)file.Length];
            file.ReadExactly(bytes);
            var version = Convert.ToHexString(SHA256.HashData(bytes));
            try
            {
                using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return Invalid();
                var names = root.EnumerateObject().Select(property => property.Name).ToArray();
                if (names.Distinct(StringComparer.Ordinal).Count() != names.Length
                    || names.Except([nameof(Enabled), nameof(NodePath), nameof(ServerPath), nameof(UserDataDirectory),
                        nameof(VisualEnabled), nameof(VisualGrantId), nameof(AuthorizationId)], StringComparer.Ordinal).Any()
                    || !root.TryGetProperty(nameof(Enabled), out var enabled)
                    || enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return Invalid();
                var settings = root.Deserialize<ChromeBrowserSettings>();
                if (settings is null || !ValidPath(settings.NodePath) || !ValidPath(settings.ServerPath)
                    || settings.UserDataDirectory is not null && !ValidPath(settings.UserDataDirectory)
                    || settings.AuthorizationId is not null && (!Guid.TryParseExact(settings.AuthorizationId, "N", out var id) || id == Guid.Empty)
                    || settings.VisualGrantId is not null && (!Guid.TryParseExact(settings.VisualGrantId, "N", out var visualId) || visualId == Guid.Empty)) return Invalid();
                var error = !settings.Enabled ? "BROWSER_NOT_ENABLED"
                    : inspectDependencies ? DependencyError(settings) : null;
                return new(settings, error) { SourceVersion = version };

                ChromeBrowserSetupState Invalid() => new(null, "BROWSER_SETTINGS_INVALID") { SourceVersion = version };
            }
            catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException)
            { return new(null, "BROWSER_SETTINGS_INVALID") { SourceVersion = version }; }
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        { return new(null, "BROWSER_NOT_CONFIGURED"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return new(null, "BROWSER_SETTINGS_UNREADABLE"); }
        catch (ArgumentException) { return new(null, "BROWSER_SETTINGS_INVALID"); }
    }

    internal static bool ValidPath(string? path) => path is { Length: > 0 and <= 4096 }
        && !path.Any(char.IsControl) && Path.IsPathFullyQualified(path);

    internal static string? DependencyError(ChromeBrowserSettings settings)
        => !File.Exists(settings.NodePath) || !File.Exists(settings.ServerPath) ? "BROWSER_DEPENDENCY_MISSING"
            : settings.UserDataDirectory is { } directory && !Directory.Exists(directory) ? "BROWSER_PROFILE_MISSING" : null;
}

/// <summary>Local settings state. Ready means dependencies exist; it does not claim Chrome is connected.</summary>
public sealed record ChromeBrowserSetupState(ChromeBrowserSettings? Settings, string? ErrorCode)
{
    public bool Enabled => Settings?.Enabled == true;
    public bool IsAvailable => ErrorCode is null;
    internal string? SourceVersion { get; init; }
}

public sealed record BrowserDiagnostic(DateTimeOffset Timestamp, string Correlation, string Stage, string? ErrorCode);

internal interface IChromeMcpConnection : IAsyncDisposable
{
    ValueTask<System.Text.Json.JsonElement> CallAsync(ChromeBrowserSettings settings, string name,
        System.Text.Json.JsonElement arguments, CancellationToken token);
    ValueTask ResetAsync();
}

internal sealed class BrowserException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
