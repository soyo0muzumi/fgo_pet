using System.Text.Json;

namespace FgoPet.Plugin.Browser;

/// <summary>Local host grant. Never supplied by a model, page, package or tool argument.</summary>
public sealed record ChromeBrowserSettings(bool Enabled, string NodePath, string ServerPath, string? UserDataDirectory = null)
{
    public const string ServerVersion = "1.10.1";
    public static ChromeBrowserSettings? Load(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length > 8192) return null;
            var settings = JsonSerializer.Deserialize<ChromeBrowserSettings>(file);
            return settings is { Enabled: true } && Path.IsPathFullyQualified(settings.NodePath)
                && Path.IsPathFullyQualified(settings.ServerPath)
                && (settings.UserDataDirectory is null || Path.IsPathFullyQualified(settings.UserDataDirectory)) ? settings : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { return null; }
    }
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
