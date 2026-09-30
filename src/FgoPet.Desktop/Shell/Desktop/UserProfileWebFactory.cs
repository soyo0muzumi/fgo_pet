using System.IO;
using System.Text.Json;
using FgoPet.App.Theming;
using FgoPet.Character.Settings;
using FgoPet.UiSdk;

namespace FgoPet.App.Settings;

/// <summary>The user-profile Settings page, backed by the existing profile owner.</summary>
public sealed class UserProfileWebFactory(
    ICharacterSettingsStore settings, UserProfileViewModel viewModel, string storageRoot,
    ThemeService? theme = null)
{
    private static readonly WebSurfacePolicy Policy = new(
        "https://settings.fgopet.invalid/index.html",
        ["getProfile", "saveProfile", "resetProfile"]);

    public WebView2SurfaceHost CreateView()
    {
        var host = new WebView2SurfaceHost(Policy,
            Path.Combine(AppContext.BaseDirectory, "Desktop", "ui", "settings", "user-profile"),
            Path.Combine(storageRoot, "WebView2", "settings", "user-profile"), HandleCommandAsync)
        { MinHeight = 380 };
        if (theme is not null)
        {
            var version = 1L;
            EventHandler changed = (_, _) => host.SetThemeVersion(
                Interlocked.Increment(ref version), theme.CaptureWebPalette());
            theme.ThemeChanged += changed;
            host.Disposed += (_, _) => theme.ThemeChanged -= changed;
            host.SetThemeVersion(version, theme.CaptureWebPalette());
        }
        return host;
    }

    internal ValueTask<WebSurfaceCommandResult> HandleCommandAsync(WebSurfaceMessage message,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return ValueTask.FromResult(message.Type switch
            {
                "getProfile" => new WebSurfaceCommandResult(true, Snapshot()),
                "saveProfile" => Save(message.Payload),
                "resetProfile" => Reset(),
                _ => new WebSurfaceCommandResult(false, ErrorCode: "SETTINGS_UNKNOWN_COMMAND"),
            });
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromResult(new WebSurfaceCommandResult(false, ErrorCode: "SETTINGS_UNAVAILABLE"));
        }
    }

    private object Snapshot() => new
    {
        displayName = settings.Load().UserProfile?.DisplayName ?? string.Empty,
        explanation = viewModel.ProfileOnlyExplanation,
    };

    private WebSurfaceCommandResult Save(JsonElement payload)
    {
        if (!payload.TryGetProperty("displayName", out var field)
            || field.ValueKind != JsonValueKind.String || field.GetString() is not { } name
            || name.Length > 80)
            return new(false, ErrorCode: "SETTINGS_INVALID_INPUT");
        viewModel.DisplayName = name;
        viewModel.SaveCommand.Execute(null);
        return string.IsNullOrEmpty(viewModel.ErrorText)
            ? new(true, Snapshot())
            : new(false, new { message = viewModel.ErrorText }, "SETTINGS_SAVE_FAILED");
    }

    private WebSurfaceCommandResult Reset()
    {
        viewModel.ResetCommand.Execute(null);
        return string.IsNullOrEmpty(viewModel.ErrorText)
            ? new(true, Snapshot())
            : new(false, new { message = viewModel.ErrorText }, "SETTINGS_SAVE_FAILED");
    }
}
