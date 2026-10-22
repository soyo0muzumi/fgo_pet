using System.Text.Json;
using FgoPet.UiSdk;

namespace FgoPet.Plugin.Qq.Desktop;

/// <summary>Local settings commands only; remote sender and conversation identities are host-owned.</summary>
public sealed class QqSettingsWebPage : ISettingsWebPage
{
    private static readonly IReadOnlyList<string> PageCommands = Array.AsReadOnly(new[]
    {
        "qq.get", "qq.configure", "qq.beginPairing", "qq.setEnabled", "qq.bindCurrentConversation", "qq.reset",
    });

    private readonly Func<CancellationToken, Task<QqChannelSnapshot>> _get;
    private readonly Func<string, string?, CancellationToken, Task<QqChannelSnapshot>> _configure;
    private readonly Func<CancellationToken, Task<QqChannelSnapshot>> _beginPairing;
    private readonly Func<bool, CancellationToken, Task<QqChannelSnapshot>> _setEnabled;
    private readonly Func<CancellationToken, Task<QqChannelSnapshot>> _bindCurrentConversation;
    private readonly Func<CancellationToken, Task<QqChannelSnapshot>> _reset;

    public QqSettingsWebPage(QqChatPlugin owner)
        : this((owner ?? throw new ArgumentNullException(nameof(owner))).GetSnapshotAsync,
            owner.ConfigureAsync, owner.BeginPairingAsync, owner.SetEnabledAsync,
            owner.BindCurrentConversationAsync, owner.ResetAsync)
    {
    }

    internal QqSettingsWebPage(Func<CancellationToken, Task<QqChannelSnapshot>> get,
        Func<string, string?, CancellationToken, Task<QqChannelSnapshot>> configure,
        Func<CancellationToken, Task<QqChannelSnapshot>> beginPairing,
        Func<bool, CancellationToken, Task<QqChannelSnapshot>> setEnabled,
        Func<CancellationToken, Task<QqChannelSnapshot>> bindCurrentConversation,
        Func<CancellationToken, Task<QqChannelSnapshot>> reset)
    {
        _get = get ?? throw new ArgumentNullException(nameof(get));
        _configure = configure ?? throw new ArgumentNullException(nameof(configure));
        _beginPairing = beginPairing ?? throw new ArgumentNullException(nameof(beginPairing));
        _setEnabled = setEnabled ?? throw new ArgumentNullException(nameof(setEnabled));
        _bindCurrentConversation = bindCurrentConversation ?? throw new ArgumentNullException(nameof(bindCurrentConversation));
        _reset = reset ?? throw new ArgumentNullException(nameof(reset));
    }

    public string SettingsPageId => "Qq";
    public string ModulePath => "pages/qq.js";
    public IReadOnlyList<string> Commands => PageCommands;

    public async ValueTask<WebSurfaceCommandResult> HandleCommandAsync(WebSurfaceMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = message.Payload;
            if (payload.ValueKind != JsonValueKind.Object
                || !payload.TryGetProperty("pageId", out var page)
                || page.ValueKind != JsonValueKind.String
                || page.GetString() != SettingsPageId)
                return InvalidInput();

            Task<QqChannelSnapshot> operation;
            switch (message.Type)
            {
                case "qq.get" when Only(payload, "pageId"):
                    operation = _get(cancellationToken);
                    break;
                case "qq.configure":
                    if (!Only(payload, "pageId", "appId", "appSecret")
                        || !payload.TryGetProperty("appId", out var appIdElement)
                        || appIdElement.ValueKind != JsonValueKind.String
                        || appIdElement.GetString() is not { } appId)
                        return InvalidInput();
                    appId = appId.Trim();
                    if (appId.Length is < 1 or > 32 || appId.Any(c => c is < '0' or > '9'))
                        return InvalidInput();
                    string? secret = null;
                    if (payload.TryGetProperty("appSecret", out var secretElement)
                        && secretElement.ValueKind != JsonValueKind.Null)
                    {
                        if (secretElement.ValueKind != JsonValueKind.String
                            || secretElement.GetString() is not { Length: <= 4096 } text
                            || text.Any(char.IsControl))
                            return InvalidInput();
                        secret = text;
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    operation = _configure(appId, secret, cancellationToken);
                    break;
                case "qq.beginPairing" when Only(payload, "pageId"):
                    operation = _beginPairing(cancellationToken);
                    break;
                case "qq.setEnabled":
                    if (!Only(payload, "pageId", "enabled")
                        || !payload.TryGetProperty("enabled", out var enabled)
                        || enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        return InvalidInput();
                    cancellationToken.ThrowIfCancellationRequested();
                    operation = _setEnabled(enabled.GetBoolean(), cancellationToken);
                    break;
                case "qq.bindCurrentConversation" when Only(payload, "pageId"):
                    operation = _bindCurrentConversation(cancellationToken);
                    break;
                case "qq.reset" when Only(payload, "pageId"):
                    operation = _reset(cancellationToken);
                    break;
                default:
                    return PageCommands.Contains(message.Type, StringComparer.Ordinal)
                        ? InvalidInput() : new(false, ErrorCode: "SETTINGS_UNKNOWN_COMMAND");
            }

            var snapshot = await operation.ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            return new(true, snapshot);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ArgumentException) when (!cancellationToken.IsCancellationRequested)
        {
            return InvalidInput();
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Exception text can contain credentials or provider response data.
            return new(false, ErrorCode: "QQ_SETTINGS_UNAVAILABLE");
        }
    }

    private static WebSurfaceCommandResult InvalidInput() => new(false, ErrorCode: "SETTINGS_INVALID_INPUT");

    private static bool Only(JsonElement payload, params string[] names)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return payload.EnumerateObject().All(property =>
            names.Contains(property.Name, StringComparer.Ordinal) && seen.Add(property.Name));
    }
}
