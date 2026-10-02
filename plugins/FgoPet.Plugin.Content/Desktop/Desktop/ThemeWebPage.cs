using System.Text.Json;
using FgoPet.App.Theming;
using FgoPet.Core.Settings;
using FgoPet.UiSdk;

namespace FgoPet.App.Settings;

/// <summary>Exposes the existing ThemeService selection through the shared Settings Web root.</summary>
public sealed class ThemeWebPage : ISettingsWebPage
{
    private static readonly string[] PageCommands = ["theme.get", "theme.select"];
    private readonly ThemeService _themeService;

    public ThemeWebPage(ThemeService themeService)
    {
        _themeService = themeService ?? throw new ArgumentNullException(nameof(themeService));
    }

    public string SettingsPageId => "Theme";

    public string ModulePath => "pages/theme.js";

    public IReadOnlyList<string> Commands => PageCommands;

    public ISettingsWebPage CreateSession() => new ThemeWebPage(_themeService);

    public ValueTask<WebSurfaceCommandResult> HandleCommandAsync(WebSurfaceMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();

        switch (message.Type)
        {
            case "theme.get":
                if (!HasOnlyProperties(message.Payload, "pageId")
                    || !HasPageId(message.Payload))
                    return Result(false, errorCode: "SETTINGS_INVALID_INPUT");
                return Result(true, Snapshot());

            case "theme.select":
                if (!HasOnlyProperties(message.Payload, "pageId", "theme")
                    || !HasPageId(message.Payload)
                    || !message.Payload.TryGetProperty("theme", out var value)
                    || value.ValueKind != JsonValueKind.String)
                    return Result(false, errorCode: "SETTINGS_INVALID_INPUT");

                var selected = value.GetString() switch
                {
                    "System" => AppTheme.System,
                    "FgoLight" => AppTheme.FgoLight,
                    "FgoDark" => AppTheme.FgoDark,
                    _ => (AppTheme?)null,
                };
                if (selected is null)
                    return Result(false, errorCode: "SETTINGS_INVALID_INPUT");

                cancellationToken.ThrowIfCancellationRequested();
                _themeService.Select(selected.Value);
                return Result(true, Snapshot());

            default:
                return Result(false, errorCode: "SETTINGS_UNKNOWN_COMMAND");
        }
    }

    private object Snapshot() => new
    {
        selected = ThemeToken(_themeService.SelectedTheme),
        effective = ThemeToken(_themeService.CurrentTheme),
        statusText = _themeService.StatusText,
    };

    private bool HasPageId(JsonElement payload) =>
        payload.TryGetProperty("pageId", out var value)
        && value.ValueKind == JsonValueKind.String
        && string.Equals(value.GetString(), SettingsPageId, StringComparison.Ordinal);

    private static bool HasOnlyProperties(JsonElement payload, params string[] allowed)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return false;

        var remaining = new HashSet<string>(allowed, StringComparer.Ordinal);
        foreach (var property in payload.EnumerateObject())
        {
            if (!remaining.Remove(property.Name))
                return false;
        }

        return remaining.Count == 0;
    }

    private static string ThemeToken(AppTheme theme) => theme switch
    {
        AppTheme.System => "System",
        AppTheme.FgoLight => "FgoLight",
        AppTheme.FgoDark => "FgoDark",
        _ => "FgoDark",
    };

    private static ValueTask<WebSurfaceCommandResult> Result(bool success, object? payload = null,
        string? errorCode = null) => ValueTask.FromResult(new WebSurfaceCommandResult(success, payload, errorCode));
}
