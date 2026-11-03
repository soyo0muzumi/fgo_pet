using System.IO;
using System.Text.Json;
using FgoPet.Extensibility;
using FgoPet.Plugin.Browser;
using FgoPet.Plugin.WebSearch;
using FgoPet.UiSdk;

namespace FgoPet.App.Settings;

/// <summary>Local, explicitly requested setup actions. Opening the page does not connect or grant access.</summary>
public sealed class WebAccessSettingsWebPage(
    WebSearchSettingsStore searchSettings,
    ChromeBrowserSettingsStore browserSettings,
    Func<CancellationToken, ValueTask<ToolAvailability>> checkSearch,
    Func<CancellationToken, ValueTask<ToolAvailability>> checkBrowser,
    Func<CancellationToken, ValueTask<ToolAvailability>> reconnectBrowser,
    string applicationDirectory) : ISettingsWebPage
{
    private static readonly IReadOnlyList<string> PageCommands = Array.AsReadOnly(new[]
    {
        "webAccess.get", "webAccess.search.setEnabled", "webAccess.search.check",
        "webAccess.browser.useBundled", "webAccess.browser.save", "webAccess.browser.setEnabled",
        "webAccess.browser.check", "webAccess.browser.reconnect",
    });
    private readonly SemaphoreSlim _commands = new(1, 1);
    private Check? _searchCheck, _browserCheck;

    public string SettingsPageId => "WebAccess";
    public string ModulePath => "pages/web-access.js";
    public IReadOnlyList<string> Commands => PageCommands;

    public ISettingsWebPage CreateSession() => new WebAccessSettingsWebPage(
        searchSettings, browserSettings, checkSearch, checkBrowser, reconnectBrowser, applicationDirectory);

    public async ValueTask<WebSurfaceCommandResult> HandleCommandAsync(WebSurfaceMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();
        var payload = message.Payload;
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("pageId", out var page) || page.ValueKind != JsonValueKind.String
            || page.GetString() != SettingsPageId) return InvalidInput();
        await _commands.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (message.Type)
            {
                case "webAccess.get" when Only(payload, "pageId"):
                    break;
                case "webAccess.search.setEnabled":
                    if (!Only(payload, "pageId", "enabled") || !Boolean(payload, "enabled", out var searchEnabled))
                        return InvalidInput();
                    var searchWrite = await searchSettings.SetEnabledAsync(searchEnabled, cancellationToken);
                    _searchCheck = null;
                    if (WriteFailed(searchWrite.ErrorCode)) return new(false, ErrorCode: searchWrite.ErrorCode);
                    break;
                case "webAccess.search.check" when Only(payload, "pageId"):
                    var searchGrant = searchSettings.Load();
                    var searchResult = await checkSearch(cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    _searchCheck = searchGrant == searchSettings.Load() ? new(searchGrant, searchResult, DateTimeOffset.UtcNow) : null;
                    break;
                case "webAccess.browser.useBundled" when Only(payload, "pageId"):
                    var bundled = await browserSettings.SaveAsync(BundledNode, BundledServer, null, true, cancellationToken);
                    _browserCheck = null;
                    if (WriteFailed(bundled.ErrorCode)) return new(false, ErrorCode: bundled.ErrorCode);
                    break;
                case "webAccess.browser.save":
                    if (!Only(payload, "pageId", "nodePath", "serverPath", "userDataDirectory")
                        || !Text(payload, "nodePath", out var nodePath) || !Text(payload, "serverPath", out var serverPath)
                        || !OptionalText(payload, "userDataDirectory", out var profile)) return InvalidInput();
                    var browserWrite = await browserSettings.SaveAsync(nodePath, serverPath, profile,
                        enabled: null, cancellationToken);
                    _browserCheck = null;
                    if (WriteFailed(browserWrite.ErrorCode)) return new(false, ErrorCode: browserWrite.ErrorCode);
                    break;
                case "webAccess.browser.setEnabled":
                    if (!Only(payload, "pageId", "enabled") || !Boolean(payload, "enabled", out var browserEnabled))
                        return InvalidInput();
                    var browserToggle = await browserSettings.SetEnabledAsync(browserEnabled, cancellationToken);
                    _browserCheck = null;
                    if (WriteFailed(browserToggle.ErrorCode) || browserToggle.ErrorCode == "BROWSER_NOT_CONFIGURED")
                        return new(false, ErrorCode: browserToggle.ErrorCode);
                    break;
                case "webAccess.browser.check" when Only(payload, "pageId"):
                case "webAccess.browser.reconnect" when Only(payload, "pageId"):
                    var browserGrant = browserSettings.Read().Settings;
                    var browserResult = await (message.Type.EndsWith("reconnect", StringComparison.Ordinal)
                        ? reconnectBrowser(cancellationToken) : checkBrowser(cancellationToken));
                    cancellationToken.ThrowIfCancellationRequested();
                    _browserCheck = browserGrant == browserSettings.Read().Settings
                        ? new(browserGrant, browserResult, DateTimeOffset.UtcNow) : null;
                    break;
                default:
                    return PageCommands.Contains(message.Type, StringComparer.Ordinal)
                        ? InvalidInput() : new(false, ErrorCode: "SETTINGS_UNKNOWN_COMMAND");
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new(true, Snapshot());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { return new(false, ErrorCode: "WEB_ACCESS_SETTINGS_UNAVAILABLE"); }
        finally { _commands.Release(); }
    }

    private WebAccessSnapshot Snapshot()
    {
        var search = searchSettings.GetSnapshot();
        var browser = browserSettings.Read();
        var searchCheck = search.Available && Equals(_searchCheck?.Grant, searchSettings.Load()) ? _searchCheck : null;
        var browserCheck = browser.IsAvailable && Equals(_browserCheck?.Grant, browser.Settings) ? _browserCheck : null;
        var settings = browser.Settings;
        return new(new(search.Configured, search.Enabled, search.Available,
                search.ErrorCode ?? searchCheck?.Result.ErrorCode, searchCheck?.CheckedAtUtc, searchCheck?.Result.IsAvailable),
            new(settings is not null, browser.Enabled, browser.IsAvailable,
                browser.ErrorCode ?? browserCheck?.Result.ErrorCode, browserCheck?.CheckedAtUtc, browserCheck?.Result.IsAvailable,
                settings?.NodePath ?? BundledNode, settings?.ServerPath ?? BundledServer, settings?.UserDataDirectory,
                settings?.VisualEnabled == true));
    }

    private string BundledNode => Path.Combine(applicationDirectory, "Browser", "node.exe");
    private string BundledServer => Path.Combine(applicationDirectory, "Browser", "node_modules", "chrome-devtools-mcp",
        "build", "src", "bin", "chrome-devtools-mcp.js");

    private static bool WriteFailed(string? code) => code is not null
        && (code.EndsWith("WRITE_FAILED", StringComparison.Ordinal) || code.EndsWith("CHANGED", StringComparison.Ordinal)
            || code.EndsWith("INVALID", StringComparison.Ordinal) || code.EndsWith("UNREADABLE", StringComparison.Ordinal));
    private static WebSurfaceCommandResult InvalidInput() => new(false, ErrorCode: "SETTINGS_INVALID_INPUT");
    private static bool Only(JsonElement value, params string[] names)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return value.EnumerateObject().All(property => names.Contains(property.Name, StringComparer.Ordinal) && seen.Add(property.Name));
    }
    private static bool Boolean(JsonElement value, string name, out bool result)
    {
        result = false;
        if (!value.TryGetProperty(name, out var property) || property.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        result = property.GetBoolean();
        return true;
    }
    private static bool Text(JsonElement value, string name, out string result)
    {
        result = "";
        if (!value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String
            || property.GetString() is not { Length: > 0 and <= 4096 } text || text.Any(char.IsControl)) return false;
        result = text;
        return true;
    }
    private static bool OptionalText(JsonElement value, string name, out string? result)
    {
        result = null;
        if (!value.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null) return true;
        if (property.ValueKind == JsonValueKind.String && property.GetString() is { Length: <= 4096 } text && !text.Any(char.IsControl))
        { result = string.IsNullOrWhiteSpace(text) ? null : text; return true; }
        return false;
    }
    private sealed record Check(object? Grant, ToolAvailability Result, DateTimeOffset CheckedAtUtc);
}

public sealed record WebAccessSnapshot(WebAccessServiceState Search, WebAccessBrowserState Browser);
public sealed record WebAccessServiceState(bool Configured, bool Enabled, bool Ready, string? ErrorCode,
    DateTimeOffset? CheckedAtUtc, bool? CheckSucceeded);
public sealed record WebAccessBrowserState(bool Configured, bool Enabled, bool Ready, string? ErrorCode,
    DateTimeOffset? CheckedAtUtc, bool? CheckSucceeded, string NodePath, string ServerPath, string? UserDataDirectory,
    bool VisualEnabled);
