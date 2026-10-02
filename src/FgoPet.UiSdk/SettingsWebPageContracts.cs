namespace FgoPet.UiSdk;

/// <summary>Describes a Web settings module and routes only its named commands to its owner.</summary>
public interface ISettingsWebPage
{
    string SettingsPageId { get; }
    string ModulePath { get; }
    IReadOnlyList<string> Commands { get; }

    /// <summary>
    /// Creates the command handler bound to one Settings root host. Stateless owners may return
    /// themselves; owners with mutable page state should return an independent handler.
    /// </summary>
    ISettingsWebPage CreateSession() => this;

    /// <summary>
    /// Handles a request for the current owner route. An owner that awaits before changing state
    /// must check the supplied token immediately before committing, and must not commit after
    /// cancellation. The token is cancelled when the route changes or its host closes.
    /// </summary>
    ValueTask<WebSurfaceCommandResult> HandleCommandAsync(WebSurfaceMessage message,
        CancellationToken cancellationToken);
}
